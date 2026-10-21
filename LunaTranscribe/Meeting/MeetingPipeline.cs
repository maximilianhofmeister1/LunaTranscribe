using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Engine;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LunaTranscribe.Meeting;

/// <summary>Ein erkannter Redebeitrag, wie ihn die Pipeline liefert (Sprecher-ID beliebig, Zeiten in Sekunden).</summary>
public sealed record MeetingTurnResult(string SpeakerId, double Start, double End, string Text);

/// <summary>
/// Nahtstelle zwischen Warteschlange und Verarbeitung einer Meeting-Aufnahme (Sprecher erkennen, Beiträge
/// transkribieren). Die echte Umsetzung (<c>MeetingTranscriber</c> mit <c>SpeakerDiarizer</c>) wird in
/// <c>AppState.CreateMeetingPipeline</c> eingesetzt; die Warteschlange kennt nur diese Schnittstelle.
/// Läuft auf einem Hintergrund-Thread. Die Warteschlange nummeriert die Sprecher danach als „Sprecher 1…n“
/// nach erstem Auftreten und baut daraus das <see cref="MeetingTranscript"/>.
/// </summary>
public interface IMeetingPipeline
{
    /// <param name="wavPath">Aufnahme in der Warteschlange (16 kHz mono WAV); nur lesen, nie verschieben oder löschen.</param>
    /// <param name="expectedSpeakers">Bekannte Teilnehmerzahl, 0 = automatisch erkennen.</param>
    /// <param name="engine">Geladene Diktier-Engine; die Warteschlange hält sie während des Laufs geladen.</param>
    /// <param name="progress">Schritt („Sprecher werden erkannt…“) und Gesamtfortschritt 0…1.</param>
    Task<IReadOnlyList<MeetingTurnResult>> RunAsync(string wavPath, AppLanguage language, int expectedSpeakers,
        ITranscriptionEngine engine, IProgress<(string Step, double Fraction)> progress, CancellationToken ct);
}

/// <summary>
/// Übergangs-Pipeline ohne Sprechererkennung: transkribiert die ganze Aufnahme mit der Diktier-Engine,
/// in Abschnitten von etwa 20 s (geschnitten an der leisesten Stelle), alles als ein Sprecher. Bis der echte
/// <c>MeetingTranscriber</c> eingesetzt ist, liefert sie so wenigstens ein vollständiges Transkript.
/// </summary>
public sealed class WholeFilePipeline : IMeetingPipeline
{
    private const double TargetChunkSeconds = 20;
    private const double MinChunkSeconds = 14;
    private const double MaxChunkSeconds = 26;

    public async Task<IReadOnlyList<MeetingTurnResult>> RunAsync(string wavPath, AppLanguage language, int expectedSpeakers,
        ITranscriptionEngine engine, IProgress<(string Step, double Fraction)> progress, CancellationToken ct)
    {
        progress.Report(("Aufnahme wird gelesen…", 0));
        var samples = WavReader.ReadMono16k(wavPath);
        ct.ThrowIfCancellationRequested();

        var chunks = Chunk(samples);
        var result = new List<MeetingTurnResult>();
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(($"Transkribiere Abschnitt {i + 1} von {chunks.Count}…", 0.05 + 0.95 * i / chunks.Count));
            var (start, end) = chunks[i];
            var slice = new float[end - start];
            Array.Copy(samples, start, slice, 0, slice.Length);
            var text = await engine.TranscribeAsync(slice, language, ct);
            if (string.IsNullOrWhiteSpace(text)) continue;
            result.Add(new MeetingTurnResult("Sprecher 1", (double)start / WavReader.SampleRate, (double)end / WavReader.SampleRate, text.Trim()));
        }
        progress.Report(("Fertig", 1));
        return result;
    }

    /// <summary>Teilt in Abschnitte von 14–26 s; Schnitt an der leisesten 100-ms-Stelle um 20 s.</summary>
    internal static List<(int Start, int End)> Chunk(float[] samples)
    {
        const int rate = WavReader.SampleRate;
        const int frame = rate / 10;
        var chunks = new List<(int, int)>();
        var start = 0;
        while (start < samples.Length)
        {
            if (samples.Length - start <= MaxChunkSeconds * rate)
            {
                chunks.Add((start, samples.Length));
                break;
            }
            var best = start + (int)(TargetChunkSeconds * rate);
            var bestEnergy = double.MaxValue;
            for (var pos = start + (int)(MinChunkSeconds * rate); pos + frame <= start + MaxChunkSeconds * rate; pos += frame)
            {
                double energy = 0;
                for (var k = pos; k < pos + frame; k++) energy += samples[k] * samples[k];
                if (energy < bestEnergy) { bestEnergy = energy; best = pos + frame / 2; }
            }
            chunks.Add((start, best));
            start = best;
        }
        return chunks;
    }
}

/// <summary>
/// Nur für Selbsttests: liefert ohne Modell abwechselnd zwei Sprecher mit Platzhaltertext und meldet
/// Fortschritt wie eine echte Pipeline. Die Engine wird nicht benutzt.
/// </summary>
public sealed class FakeMeetingPipeline : IMeetingPipeline
{
    public TimeSpan StepDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public async Task<IReadOnlyList<MeetingTurnResult>> RunAsync(string wavPath, AppLanguage language, int expectedSpeakers,
        ITranscriptionEngine engine, IProgress<(string Step, double Fraction)> progress, CancellationToken ct)
    {
        var duration = WavReader.DurationSeconds(wavPath);
        progress.Report(("Sprecher werden erkannt…", 0.1));
        await Task.Delay(StepDelay, ct);
        var speakers = expectedSpeakers > 0 ? expectedSpeakers : 2;
        var count = Math.Max(1, (int)Math.Ceiling(duration / 10));
        var turns = new List<MeetingTurnResult>();
        for (var i = 0; i < count; i++)
        {
            progress.Report(($"Transkribiere Beitrag {i + 1} von {count}…", 0.4 + 0.6 * i / count));
            await Task.Delay(StepDelay, ct);
            var start = i * 10.0;
            turns.Add(new MeetingTurnResult($"spk{i % speakers}", start, Math.Min(duration, start + 10),
                $"Testbeitrag {i + 1} aus der Fake-Pipeline."));
        }
        progress.Report(("Fertig", 1));
        return turns;
    }
}

/// <summary>Liest WAV-Dateien als 16 kHz mono Float32 (NAudio; mischt und resampelt bei Bedarf).</summary>
public static class WavReader
{
    public const int SampleRate = 16_000;

    public static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (provider.WaveFormat.Channels > 2) throw new InvalidDataException("Die Aufnahme hat mehr als zwei Kanäle.");
        if (provider.WaveFormat.SampleRate != SampleRate) provider = new WdlResamplingSampleProvider(provider, SampleRate);

        var result = new List<float>((int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * SampleRate + SampleRate));
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            result.AddRange(new ArraySegment<float>(buffer, 0, read));
        return result.ToArray();
    }

    public static double DurationSeconds(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }

    /// <summary>Schreibt 16 kHz mono 16 bit (für Selbsttests).</summary>
    public static void WriteMono16k(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
