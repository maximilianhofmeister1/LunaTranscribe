using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Entwickler-Einstieg für die Hintergrund-Verarbeitung – ohne Mikrofon, direkt aus dem Terminal:
/// <code>LunaTranscribe.exe --queue-selftest a.wav [b.wav …] [--fake]</code>
/// Kopiert jede Datei als beendetes Meeting in eine Warteschlange (so, als hätte man mehrere Meetings direkt
/// hintereinander beendet), wartet, bis alle verarbeitet sind, und druckt Reihenfolge, Zeiten und Ergebnis.
///
/// Ohne <c>--fake</c> läuft die echte Pipeline der App (<see cref="TranscriberPipeline"/>: Sprechererkennung +
/// Transkription je Beitrag mit der eingestellten Engine) – die Modelle werden dafür geladen. <c>--fake</c> nutzt <see cref="FakeMeetingPipeline"/> ohne Modell (prüft nur den Ablauf).
///
/// Danach zwei Invarianten: Ein Auftrag übersteht einen Neustart (neue Warteschlange auf demselben Ordner nimmt ihn
/// wieder auf), und „Verwerfen“ während der Verarbeitung löscht Aufnahme und Auftrag. Alles läuft in einem
/// temporären Ordner (Bibliothek, Rettungsordner, Warteschlange); Einstellungen und echte Warteschlange bleiben unberührt.
/// </summary>
public static class QueueSelfTest
{
    [SelfTest("--queue-selftest")]
    private static async Task<int> Run(string[] args)
    {
        var fake = args.Contains("--fake");
        var files = args.Where(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToList();
        if (files.Count == 0)
        {
            SelfTests.Print("Aufruf: --queue-selftest <datei.wav> [weitere.wav …] [--fake]");
            return 2;
        }
        var missing = files.FirstOrDefault(f => !File.Exists(f));
        if (missing != null)
        {
            SelfTests.Print("FEHLER: Datei nicht gefunden: " + missing);
            return 2;
        }

        var temp = Path.Combine(Path.GetTempPath(), "luna-queue-" + Guid.NewGuid().ToString("N"));
        var dispatcher = Application.Current.Dispatcher;
        var settings = SettingsStore.Shared;
        var models = new ModelManager(dispatcher);
        try
        {
            var store = new MeetingStore(settings, dispatcher, Path.Combine(temp, "Bibliothek"), Path.Combine(temp, "Unsaved"));
            var queueFolder = Path.Combine(temp, "Queue");
            IMeetingPipeline pipeline = fake ? new FakeMeetingPipeline() : new TranscriberPipeline();
            var queue = Create(settings, models, store, pipeline, queueFolder, fake);
            models.CanUnload = () => !queue.IsProcessing;
            SelfTests.Print($"Pipeline: {(fake ? "Fake (ohne Modell)" : $"{pipeline.GetType().Name} mit {settings.Engine.ShortLabel()}")}");

            var ok = await ProcessAll(queue, store, settings, files, temp);
            ok &= await CheckRestore(settings, models, store, pipeline, queueFolder, fake, files[0], temp);
            ok &= await CheckDiscard(queue, files[0], temp, queueFolder);
            SelfTests.Print(ok ? "\nOK" : "\nFEHLGESCHLAGEN");
            return ok ? 0 : 1;
        }
        finally
        {
            models.Shutdown();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static MeetingProcessingQueue Create(SettingsStore settings, ModelManager models, MeetingStore store,
                                                 IMeetingPipeline pipeline, string queueFolder, bool fake)
    {
        var queue = new MeetingProcessingQueue(settings, models, store, pipeline, queueFolder);
        if (fake) queue.EngineProvider = _ => Task.FromResult<ITranscriptionEngine>(new NullEngine());
        return queue;
    }

    private static async Task<bool> ProcessAll(MeetingProcessingQueue queue, MeetingStore store, SettingsStore settings,
                                               List<string> files, string temp)
    {
        var started = Stopwatch.StartNew();
        var finished = new List<string>();
        void OnFinished(string title, Guid _)
        {
            finished.Add(title);
            SelfTests.Print($"  fertig nach {started.Elapsed.TotalSeconds,5:0.0} s: {title}");
        }
        queue.Finished += OnFinished;
        string? lastStep = null;
        queue.ProgressChanged += item =>
        {
            if (item.Step == lastStep) return;
            lastStep = item.Step;
            SelfTests.Print($"    {item.Fraction * 100,3:0} % {item.Step}");
        };
        queue.Active = true;

        // Alle Meetings direkt hintereinander „beenden“ – jedes mit eigener Aufnahmezeit.
        for (var i = 0; i < files.Count; i++)
        {
            var copy = Path.Combine(temp, $"aufnahme-{i}.wav");
            Directory.CreateDirectory(temp);
            File.Copy(files[i], copy);
            var duration = WavReader.DurationSeconds(copy);
            queue.Enqueue(copy, DateTime.UtcNow.AddMinutes(i), duration, settings.LanguageForEngine(settings.PrimaryLanguage), 0);
        }
        SelfTests.Print($"{files.Count} Meeting(s) eingereiht nach {started.Elapsed.TotalSeconds:0.00} s – die Aufnahme wäre jetzt wieder frei.");

        // Warten, bis nichts mehr wartet oder läuft (Fehler zählen als erledigt).
        while (queue.PendingCount > 0) await Task.Delay(200);
        queue.Finished -= OnFinished;

        var failures = queue.Items.Where(i => i.IsFailed).ToList();
        foreach (var item in failures)
        {
            SelfTests.Print($"  FEHLGESCHLAGEN: {item.Job.Title}: {item.Failure}");
            queue.Discard(item.Id);
        }
        SelfTests.Print("\nBibliothek:");
        foreach (var entry in store.Entries)
        {
            var t = store.Load(entry.Id);
            var text = string.Join(" ", t.Turns.Select(x => x.Text));
            SelfTests.Print($"  {entry.Title} – {t.Turns.Count} Beiträge, {t.SpeakerIDs.Count} Sprecher: {(text.Length > 160 ? text[..160] + "…" : text)}");
        }
        SelfTests.Print($"\n{finished.Count}/{files.Count} verarbeitet in {started.Elapsed.TotalSeconds:0.0} s");
        return failures.Count == 0 && finished.Count == files.Count && store.Entries.Count == files.Count;
    }

    /// <summary>Auftrag, der beim „Beenden“ der App noch wartet, läuft in einer neuen Warteschlange weiter.</summary>
    private static async Task<bool> CheckRestore(SettingsStore settings, ModelManager models, MeetingStore store, IMeetingPipeline pipeline,
                                                 string queueFolder, bool fake, string file, string temp)
    {
        var before = store.Entries.Count;
        var first = Create(settings, models, store, pipeline, queueFolder, fake);   // inaktiv = „App beendet“
        var copy = Path.Combine(temp, "neustart.wav");
        File.Copy(file, copy);
        first.Enqueue(copy, DateTime.UtcNow.AddHours(1), WavReader.DurationSeconds(copy), AppLanguage.German, 2);

        var second = Create(settings, models, store, pipeline, queueFolder, fake);
        var restored = second.Items.Count == 1 && second.Items[0].Job.ExpectedSpeakers == 2;
        SelfTests.Print($"\nNeustart: {second.Items.Count} Auftrag wiederaufgenommen (Personen: {second.Items.FirstOrDefault()?.Job.ExpectedSpeakers})");
        second.Active = true;
        while (second.PendingCount > 0) await Task.Delay(200);
        var ok = restored && store.Entries.Count == before + 1 && second.Items.Count == 0;
        SelfTests.Print(ok ? "  verarbeitet, Warteschlange leer" : $"  FEHLER: {second.Items.FirstOrDefault()?.Failure}");
        return ok;
    }

    /// <summary>„Verwerfen“ während der Verarbeitung bricht ab und löscht Aufnahme und Auftrag.</summary>
    private static async Task<bool> CheckDiscard(MeetingProcessingQueue queue, string file, string temp, string queueFolder)
    {
        var copy = Path.Combine(temp, "verwerfen.wav");
        File.Copy(file, copy);
        var id = queue.Enqueue(copy, DateTime.UtcNow.AddHours(2), WavReader.DurationSeconds(copy), AppLanguage.German, 0);
        for (var i = 0; i < 50 && !queue.IsProcessing; i++) await Task.Delay(20);
        queue.Discard(id);
        for (var i = 0; i < 100 && Directory.Exists(Path.Combine(queueFolder, id.ToString("D").ToUpperInvariant())); i++) await Task.Delay(50);
        var gone = !Directory.Exists(Path.Combine(queueFolder, id.ToString("D").ToUpperInvariant())) && queue.Items.Count == 0;
        SelfTests.Print(gone ? "Verwerfen: Aufnahme und Auftrag gelöscht" : "FEHLER: verworfener Auftrag liegt noch in der Warteschlange");
        return gone;
    }

    /// <summary>Platzhalter-Engine für die Fake-Pipeline (wird nie aufgerufen).</summary>
    private sealed class NullEngine : ITranscriptionEngine
    {
        public EngineKind Kind => EngineKind.Parakeet;
        public bool IsLoaded => true;
        public bool FilesPresent => true;
        public Task PrepareAsync(Action<double> progress, CancellationToken ct) => Task.CompletedTask;
        public Task<string> TranscribeAsync(float[] samples, AppLanguage language, CancellationToken ct) => Task.FromResult("");
        public void Unload() { }
        public void Dispose() { }
    }
}
