using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Meeting;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Summary;

/// <summary>
/// Entwickler-Einstieg für die Zusammenfassung über eine Anbieter-CLI – über denselben Code wie der Button in der App
/// (<see cref="ISummaryCli"/>, <see cref="SummaryPrompt"/>), ohne Modelle:
/// <code>LunaTranscribe.exe --summary-selftest [meeting.json] [--provider claude|antigravity|codex] [--cli C:\pfad\cli.exe]
///     [--model sonnet] [--language en] [--prompt anweisung.txt] [--repeat 20] [--app]</code>
/// <c>--provider</c> wählt die CLI (Standard: claude). Ohne Datei fasst der Test ein kurzes eingebautes Gespräch zusammen;
/// <c>--repeat n</c> hängt es n-mal aneinander (langes Meeting, z. B. für die Windows-Grenze der Befehlszeile). <c>--cli</c>
/// mit einem falschen Pfad prüft die Meldung „CLI nicht gefunden“. <c>--app</c> geht wie der Button über
/// <see cref="MeetingSummarizer"/> mit Anbieter, Pfad und Modell aus den Einstellungen (nur für diesen Lauf gesetzt) in eine
/// temporäre Bibliothek und prüft das gespeicherte <c>meeting.json</c> und <c>summary.md</c>. Achtung: Der Text geht an den Anbieter (Anthropic,
/// Google, OpenAI).
/// </summary>
public static class SummarySelfTest
{
    [SelfTest("--summary-selftest")]
    private static async Task<int> Run(string[] args)
    {
        var file = args.FirstOrDefault() is { } first && !first.StartsWith("--") ? first : null;
        var cliPath = Value("--cli", args) ?? "";
        var model = Value("--model", args);
        var languageCode = Value("--language", args);
        // Eigene Anweisung (mit {language}) zum Vergleichen von Prompt-Varianten, ohne neu zu bauen.
        var promptFile = Value("--prompt", args);
        var providerName = Value("--provider", args) ?? SummaryProvider.Claude.RawValue();
        if (SummaryProviderExtensions.FromRawValue(providerName) is not { } provider)
        {
            SelfTests.Print($"Unbekannter Anbieter „{providerName}“ – claude, antigravity oder codex.");
            return 2;
        }
        var repeat = int.TryParse(Value("--repeat", args), out var r) ? Math.Max(1, r) : 1;
        AppLanguage? language = null;
        if (languageCode != null)
        {
            language = AppLanguageExtensions.FromCode(languageCode);
            if (language == null)
            {
                SelfTests.Print("Unbekannte Sprache: " + languageCode);
                return 2;
            }
        }
        if (args.Contains("--app")) return await RunThroughApp(provider, cliPath, model, language, repeat);
        try
        {
            var transcript = file != null ? JsonCoding.Decode<MeetingTranscript>(File.ReadAllBytes(file)) : Sample(repeat);
            var cli = provider.LocateCli(cliPath) ?? throw CliException.NotFound(provider);
            SelfTests.Print($"CLI: {cli.Executable} · {await cli.VersionAsync()}");
            SelfTests.Print($"Meeting: {transcript.Title}, {transcript.Turns.Count} Beiträge, {transcript.WordCount} Wörter\n");
            var instructions = SummaryPrompt.Instructions(promptFile != null ? File.ReadAllText(promptFile) : SummaryPrompt.DefaultTemplate,
                language ?? transcript.Language);
            var input = transcript.Render(MeetingTranscript.ExportFormat.Text);
            if (args.Contains("--show-prompt")) SelfTests.Print($"--- Anweisung ---\n{instructions}\n--- Eingabe ---\n{input}\n---\n");
            var sw = Stopwatch.StartNew();
            var summary = await cli.CompleteAsync(instructions, input, model, ISummaryCli.DefaultTimeout, default);
            SelfTests.Print(summary);
            SelfTests.Print($"\n({sw.Elapsed.TotalSeconds:0.0} s)");
            return 0;
        }
        catch (Exception ex)
        {
            SelfTests.Print("FEHLER: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Wie der Button: <see cref="MeetingSummarizer"/> mit den Einstellungen, Ergebnis in einer temporären Bibliothek. Die
    /// geänderten Einstellungen werden danach wiederhergestellt.
    /// </summary>
    private static async Task<int> RunThroughApp(SummaryProvider provider, string cliPath, string? model, AppLanguage? language, int repeat)
    {
        var settings = SettingsStore.Shared;
        var saved = (settings.SummaryProvider, Path: settings.CliPath(provider), Model: settings.RawModel(provider));
        var temp = Path.Combine(Path.GetTempPath(), "luna-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            settings.SummaryProvider = provider;
            settings.SetCliPath(provider, cliPath);
            settings.SetModel(provider, model ?? "");
            var store = new MeetingStore(settings, Application.Current.Dispatcher, Path.Combine(temp, "Bibliothek"), Path.Combine(temp, "Unsaved"));
            var transcript = Sample(repeat);
            var entry = store.Add(transcript) ?? throw new InvalidOperationException("Meeting nicht gespeichert.");
            var summarizer = new MeetingSummarizer(settings, store);
            var done = new TaskCompletionSource<MeetingSummary?>();
            summarizer.Completed += (_, summary) => done.TrySetResult(summary);
            summarizer.PropertyChanged += (_, _) =>
            {
                if (!summarizer.IsRunning(transcript.Id) && summarizer.Error(transcript.Id) is { } error) done.TrySetException(new Exception(error));
            };
            var target = language ?? transcript.Language;
            var sw = Stopwatch.StartNew();
            summarizer.Summarize(transcript, target);
            SelfTests.Print($"Läuft über {summarizer.RunningProvider(transcript.Id)?.Label() ?? "?"} …");
            var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(6));

            var reloaded = store.Load(transcript.Id);
            var folder = store.Entries.First(e => e.Id == transcript.Id).Folder;
            var json = File.ReadAllText(Path.Combine(folder, MeetingLibrary.TranscriptFileName));
            var md = File.ReadAllText(Path.Combine(folder, MeetingLibrary.SummaryFileName));
            var expectedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            var checks = new (string Name, bool Ok)[]
            {
                ("Ergebnis gemeldet", result != null),
                ($"meeting.json: provider = {provider.RawValue()}", reloaded.Summary?.Provider == provider && json.Contains($"\"provider\" : \"{provider.RawValue()}\"")),
                ($"meeting.json: model = {expectedModel ?? "(keins)"}", reloaded.Summary?.Model == expectedModel),
                ($"meeting.json: language = {target.Code()}", reloaded.Summary?.Language == target),
                ("summary.md = Titelzeile + Markdown der Zusammenfassung",
                    reloaded.Summary is { } s && md.Trim() == $"# {reloaded.Title} – Zusammenfassung\n\n{s.Markdown}".Trim()),
                ("Läuft nicht mehr, kein Fehler", !summarizer.IsRunning(transcript.Id) && summarizer.Error(transcript.Id) == null),
            };
            foreach (var (name, ok) in checks) SelfTests.Print($"  {(ok ? "OK  " : "FEHL")} {name}");
            SelfTests.Print($"„{LunaTranscribe.UI.Pages.MeetingDetailView.Origin(reloaded.Summary!)}“ ({sw.Elapsed.TotalSeconds:0.0} s)");
            return checks.All(c => c.Ok) ? 0 : 1;
        }
        catch (Exception ex)
        {
            SelfTests.Print("FEHLER: " + ex.Message);
            return 1;
        }
        finally
        {
            settings.SummaryProvider = saved.SummaryProvider;
            settings.SetCliPath(provider, saved.Path);
            settings.SetModel(provider, saved.Model);
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static string? Value(string flag, string[] args)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static MeetingTranscript Sample(int repeat = 1)
    {
        var lines = new[]
        {
            ("Anna", "Guten Morgen. Heute geht es um den Launch der neuen Version."),
            ("Markus", "Die Tests laufen noch, ich schlage Montag statt Freitag vor."),
            ("Anna", "Einverstanden. Schickst du bis Freitag den Testbericht?"),
            ("Markus", "Ja. Offen ist noch, wer die Release Notes schreibt."),
        };
        var turns = Enumerable.Repeat(lines, repeat).SelectMany(l => l)
            .Select((l, i) => new MeetingTranscript.Turn(l.Item1, i * 10, i * 10 + 9, l.Item2)).ToList();
        return new MeetingTranscript(DateTime.UtcNow, turns.Count * 10, EngineKind.Parakeet, AppLanguage.German, turns, title: "Selbsttest");
    }
}
