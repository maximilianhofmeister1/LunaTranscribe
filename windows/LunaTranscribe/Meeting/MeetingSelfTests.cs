using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Audio;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Entwickler-Selbsttests der Meeting-Pipeline (Pendant zu MeetingSelfTest.swift):
///
///     LunaTranscribe.exe --diarize-selftest dialog.wav [--speakers 2] [--language de]
///     LunaTranscribe.exe --meeting-selftest 20 [--speakers 2] [--mic-only]
///
/// Beim Meeting-Test währenddessen etwas über die Lautsprecher abspielen (z. B. SAPI-Sprachausgabe),
/// damit der Loopback-Pegel sichtbar wird. Das Transkript geht nur auf die Konsole, nie ins Log.
/// </summary>
internal static class MeetingSelfTests
{
    [SelfTest("--diarize-selftest")]
    private static async Task<int> DiarizeSelfTest(string[] args)
    {
        Utf8Console();
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            SelfTests.Print("Aufruf: --diarize-selftest <datei.wav> [--speakers N] [--language de]");
            return 2;
        }
        var path = Path.GetFullPath(args[0]);
        if (!File.Exists(path)) { SelfTests.Print("Datei nicht gefunden: " + path); return 2; }

        string? temp = null;
        if (!WavFile.Is16kMono(path))
        {
            temp = Path.Combine(Path.GetTempPath(), $"luna-diarize-{Guid.NewGuid():N}.wav");
            WavFile.ConvertTo16kMono(path, temp);
            SelfTests.Print("Nach 16 kHz mono gewandelt: " + temp);
        }
        try
        {
            SelfTests.Print($"Datei: {path} ({WavFile.DurationSeconds(temp ?? path):0.0} s)");
            if (args.Contains("--no-asr")) return await DiarizeOnlyAsync(temp ?? path, Speakers(args));
            return await ProcessAsync(temp ?? path, Speakers(args), Language(args));
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch { }
        }
    }

    [SelfTest("--meeting-selftest")]
    private static async Task<int> MeetingSelfTest(string[] args)
    {
        Utf8Console();
        var seconds = args.Length > 0 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 15;
        var systemAudio = !args.Contains("--mic-only");

        // Datei bleibt liegen, damit man sie bei Problemen anhören kann.
        var path = Path.Combine(Path.GetTempPath(), $"luna-meeting-selftest-{Guid.NewGuid():N}.wav");
        using var recorder = new MeetingRecorder();
        var t0 = Stopwatch.StartNew();
        recorder.OnFirstBuffer = () => SelfTests.Print($"Erster Puffer nach {t0.Elapsed.TotalSeconds:0.00} s");
        recorder.Start(SettingsStore.Shared.InputDeviceUid, systemAudio, path);
        SelfTests.Print($"Start dauerte {t0.Elapsed.TotalSeconds:0.00} s, Systemaudio aktiv: {recorder.SystemAudioActive}");
        SelfTests.Print($"Aufnahme läuft {seconds:0} s → {path}");

        var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        float peak = 0;
        var dictationTested = false;
        var dictationAt = seconds / 3;
        for (var i = 0; i < (int)(seconds * 4); i++)
        {
            await Task.Delay(250);
            peak = Math.Max(peak, recorder.Level);
            if (i % 4 == 3)
                SelfTests.Print($"  {recorder.Elapsed.TotalSeconds,5:0.0} s  Mikro {recorder.MicLevel:0.000}  System {recorder.SystemLevel:0.000}");

            // Diktat parallel auf demselben Mikrofon (darf die Meeting-Aufnahme nicht stören).
            if (!dictationTested && wall.Elapsed.TotalSeconds >= dictationAt)
            {
                dictationTested = true;
                var dictation = new AudioRecorder();
                try
                {
                    dictation.Start(SettingsStore.Shared.InputDeviceUid);
                    await Task.Delay(1500);
                    var samples = dictation.Stop();
                    SelfTests.Print($"  Paralleles Diktat: {samples.Length / 16000.0:0.00} s aufgenommen");
                }
                catch (Exception ex)
                {
                    SelfTests.Print("  Paralleles Diktat fehlgeschlagen: " + ex.Message);
                }
            }
        }
        var cpu = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
        var recorded = recorder.Stop();
        var size = new FileInfo(path).Length;
        SelfTests.Print($"Aufgenommen: {recorded:0.0} s (Wanduhr {wall.Elapsed.TotalSeconds:0.0} s), {size / 1024.0 / 1024.0:0.00} MB " +
                        $"({size / 1024.0 / 1024.0 / Math.Max(recorded, 0.1) * 60:0.00} MB/min)");
        SelfTests.Print($"Spitzenpegel: Mischung {peak:0.000}, Mikro {recorder.MicPeak:0.000}, System {recorder.SystemPeak:0.000}");
        SelfTests.Print($"CPU während der Aufnahme: {cpu / wall.Elapsed.TotalSeconds * 100 / Environment.ProcessorCount:0.00} % " +
                        $"aller Kerne ({cpu:0.00} s CPU in {wall.Elapsed.TotalSeconds:0.0} s)");
        if (recorded < 1)
        {
            SelfTests.Print("FEHLER: Es kam kein Audio an.");
            return 1;
        }

        return await ProcessAsync(path, Speakers(args), Language(args));
    }

    /// <summary>Diarizer + Transkription mit Parakeet, druckt Schritte, Zeiten und Turns.</summary>
    private static async Task<int> ProcessAsync(string path, int speakers, AppLanguage language)
    {
        using var engine = new ParakeetEngine();
        var sw = Stopwatch.StartNew();
        SelfTests.Print("Engine wird geladen…");
        await engine.PrepareAsync(_ => { }, CancellationToken.None);
        SelfTests.Print($"Engine geladen in {sw.Elapsed.TotalSeconds:0.0} s");

        using var diarizer = new SpeakerDiarizer();
        var transcriber = new MeetingTranscriber(diarizer, engine);
        var steps = new StepTracker();
        var audioSeconds = WavFile.DurationSeconds(path);
        sw.Restart();
        IReadOnlyList<MeetingTurn> turns;
        try
        {
            turns = await transcriber.RunAsync(path, language, speakers, p => steps.Update(p.Step), CancellationToken.None);
        }
        catch (MeetingTranscriberException ex)
        {
            SelfTests.Print("FEHLER: " + ex.Message);
            return 1;
        }
        steps.Finish();
        var elapsed = sw.Elapsed.TotalSeconds;

        Console.WriteLine();
        foreach (var t in turns)
            Console.WriteLine($"[{Clock(t.Start)}–{Clock(t.End)}] {t.SpeakerId}: {t.Text}");
        Console.WriteLine();

        var count = turns.Select(t => t.SpeakerId).Distinct().Count();
        SelfTests.Print($"{turns.Count} Beiträge, {count} Sprecher" + (speakers > 0 ? $" (vorgegeben {speakers})" : " (automatisch)"));
        SelfTests.Print($"Verarbeitung: {elapsed:0.0} s für {audioSeconds:0.0} s Audio (RTF {elapsed / Math.Max(audioSeconds, 0.1):0.000})");
        foreach (var (step, seconds) in steps.Durations)
            SelfTests.Print($"  {step,-36} {seconds,6:0.0} s");
        SelfTests.Print($"Arbeitsspeicher (Spitze): {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
        return 0;
    }

    /// <summary>Nur Sprechererkennung + Turn-Bildung, ohne ASR (schnelles Abstimmen des Clusterings).</summary>
    private static async Task<int> DiarizeOnlyAsync(string path, int speakers)
    {
        using var diarizer = new SpeakerDiarizer();
        var sw = Stopwatch.StartNew();
        await diarizer.PrepareAsync(_ => { }, CancellationToken.None);
        SelfTests.Print($"Sprechermodell geladen in {sw.Elapsed.TotalSeconds:0.0} s");
        sw.Restart();
        var segments = await diarizer.DiarizeAsync(path, speakers, _ => { }, CancellationToken.None);
        var elapsed = sw.Elapsed.TotalSeconds;
        var turns = MeetingTranscriber.BuildTurns(segments, MeetingTranscriber.MaxGapWithinTurn, MeetingTranscriber.MinTurnDuration);
        foreach (var t in turns) SelfTests.Print($"  [{Clock(t.Start)}–{Clock(t.End)}] {t.SpeakerId}");
        var audioSeconds = WavFile.DurationSeconds(path);
        SelfTests.Print($"{segments.Count} Segmente → {turns.Count} Turns, {turns.Select(t => t.SpeakerId).Distinct().Count()} Sprecher; " +
                        $"Erkennung {elapsed:0.0} s für {audioSeconds:0.0} s Audio (RTF {elapsed / Math.Max(audioSeconds, 0.1):0.000})");
        return 0;
    }

    /// <summary>Umlaute auch in umgeleiteter Konsolenausgabe korrekt.</summary>
    private static void Utf8Console()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    private static int Speakers(string[] args)
    {
        var i = Array.IndexOf(args, "--speakers");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? n : 0;
    }

    private static AppLanguage Language(string[] args)
    {
        var i = Array.IndexOf(args, "--language");
        return (i >= 0 && i + 1 < args.Length ? AppLanguageExtensions.FromCode(args[i + 1]) : null) ?? AppLanguage.German;
    }

    /// <summary>Druckt jeden Verarbeitungsschritt nur einmal und misst seine Dauer (Callback kommt von beliebigen Threads).</summary>
    private sealed class StepTracker
    {
        private readonly object _lock = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private string _last = "";
        private string _group = "";
        private double _groupStart;
        public List<(string Step, double Seconds)> Durations { get; } = new();

        public void Update(string step)
        {
            lock (_lock)
            {
                if (step == _last) return;
                _last = step;
                SelfTests.Print("  " + step);
                // „Transkribiere Beitrag 3 von 12…“ zählt als ein Schritt.
                var group = step.StartsWith("Transkribiere") ? "Transkription" : step;
                if (group == _group) return;
                Close();
                _group = group;
                _groupStart = _sw.Elapsed.TotalSeconds;
            }
        }

        public void Finish()
        {
            lock (_lock) Close();
        }

        private void Close()
        {
            if (_group.Length > 0 && _group != "Fertig") Durations.Add((_group, _sw.Elapsed.TotalSeconds - _groupStart));
            _group = "";
        }
    }
}
