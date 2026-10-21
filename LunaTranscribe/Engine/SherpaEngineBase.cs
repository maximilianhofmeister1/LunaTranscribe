using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LunaTranscribe.Settings;
using SherpaOnnx;
using LunaCore;

namespace LunaTranscribe.Engine;

/// <summary>
/// Gemeinsamer Unterbau für sherpa-onnx-Engines: Download → Recognizer laden → Warm-up,
/// sowie Fensterung langer Aufnahmen. Jede Unterklasse liefert nur die Modell-Konfiguration.
/// </summary>
public abstract class SherpaEngineBase : ITranscriptionEngine
{
    public abstract EngineKind Kind { get; }
    protected abstract ModelSpec Spec { get; }

    /// <summary>Maximale Fensterlänge in Sekunden; längere Aufnahmen werden an leisen Stellen geteilt.</summary>
    protected abstract double MaxWindowSeconds { get; }

    protected abstract OfflineRecognizerConfig BuildConfig();

    /// <summary>Sprache pro Stream setzen (nur Engines, die das unterstützen).</summary>
    protected virtual void ApplyLanguage(OfflineStream stream, AppLanguage language) { }

    protected abstract bool Supports(AppLanguage language);

    private OfflineRecognizer? _recognizer;
    private readonly SemaphoreSlim _gate = new(1, 1);   // sherpa-Recognizer nicht parallel benutzen

    public bool IsLoaded => _recognizer != null;

    public bool FilesPresent => Spec.FilesPresent;

    public async Task PrepareAsync(Action<double> progress, CancellationToken ct)
    {
        if (_recognizer != null) return;

        if (!Spec.FilesPresent)
            await ModelDownloader.EnsureAsync(Spec, p => progress(p * 0.9), ct);
        progress(0.9);
        ct.ThrowIfCancellationRequested();

        var recognizer = await Task.Run(() => new OfflineRecognizer(BuildConfig()), ct);
        ct.ThrowIfCancellationRequested();
        _recognizer = recognizer;
        progress(0.95);

        // Warm-up: erste Inferenz initialisiert ONNX-Runtime-Puffer und Threads.
        // Lieber jetzt beim Start als beim ersten Diktat.
        try
        {
            await Task.Run(() => Decode(new float[16_000], AppLanguage.English), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Warm-up-Fehler sind nicht kritisch */ }
        progress(1.0);
    }

    public async Task<string> TranscribeAsync(float[] samples, AppLanguage language, CancellationToken ct)
    {
        if (_recognizer == null) throw EngineException.NotLoaded();
        if (!Supports(language)) throw EngineException.UnsupportedLanguage(language, Kind);

        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var window in AudioChunker.Split(samples, 16_000, MaxWindowSeconds))
                {
                    ct.ThrowIfCancellationRequested();
                    var text = Decode(window, language);
                    if (text.Length == 0) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(text);
                }
                return TranscriptionVocabulary.Apply(sb.ToString().Trim());
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Decode(float[] samples, AppLanguage language)
    {
        var recognizer = _recognizer ?? throw EngineException.NotLoaded();
        using var stream = recognizer.CreateStream();
        ApplyLanguage(stream, language);
        stream.AcceptWaveform(16_000, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    public void Unload()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }

    public void Dispose() => Unload();
}

/// <summary>Teilt lange Aufnahmen in Fenster, geschnitten an der leisesten Stelle der letzten Sekunden.</summary>
public static class AudioChunker
{
    public static IEnumerable<float[]> Split(float[] samples, int sampleRate, double maxSeconds)
    {
        var max = (int)(maxSeconds * sampleRate);
        if (samples.Length <= max) { yield return samples; yield break; }

        var search = (int)(5.0 * sampleRate);      // in den letzten 5 s des Fensters suchen
        var frame = sampleRate / 50;               // 20-ms-Frames
        var pos = 0;
        while (pos < samples.Length)
        {
            var remaining = samples.Length - pos;
            if (remaining <= max)
            {
                yield return samples[pos..];
                yield break;
            }

            var end = pos + max;
            var best = end;
            var bestEnergy = double.MaxValue;
            for (var s = end - search; s + frame <= end; s += frame)
            {
                double e = 0;
                for (var i = s; i < s + frame; i++) e += samples[i] * samples[i];
                if (e < bestEnergy) { bestEnergy = e; best = s + frame / 2; }
            }
            yield return samples[pos..best];
            pos = best;
        }
    }
}
