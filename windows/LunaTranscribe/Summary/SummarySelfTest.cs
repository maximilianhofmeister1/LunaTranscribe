using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Core;

namespace LunaTranscribe.Summary;

/// <summary>
/// Entwickler-Einstieg für die Zusammenfassung über die Claude CLI – über denselben Code wie der Button in der App
/// (<see cref="ClaudeCli"/>, <see cref="SummaryPrompt"/>), ohne Modelle:
/// <code>LunaTranscribe.exe --summary-selftest [meeting.json] [--cli C:\pfad\claude.exe] [--model sonnet]</code>
/// Ohne Datei fasst der Test ein kurzes eingebautes Gespräch zusammen. <c>--cli</c> mit einem falschen Pfad prüft die
/// Meldung „CLI nicht gefunden“. Achtung: Der Text geht an Anthropic.
/// </summary>
public static class SummarySelfTest
{
    [SelfTest("--summary-selftest")]
    private static async Task<int> Run(string[] args)
    {
        var file = args.FirstOrDefault() is { } first && !first.StartsWith("--") ? first : null;
        var cliPath = Value("--cli", args) ?? "";
        var model = Value("--model", args);
        try
        {
            var transcript = file != null ? JsonCoding.Decode<MeetingTranscript>(File.ReadAllBytes(file)) : Sample();
            var cli = ClaudeCli.Locate(cliPath) ?? throw ClaudeCliException.NotFound();
            SelfTests.Print($"CLI: {cli.Executable} · {await cli.VersionAsync()}");
            SelfTests.Print($"Meeting: {transcript.Title}, {transcript.Turns.Count} Beiträge, {transcript.WordCount} Wörter\n");
            var sw = Stopwatch.StartNew();
            var summary = await cli.CompleteAsync(
                SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, transcript.Language),
                transcript.Render(MeetingTranscript.ExportFormat.Text), model);
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

    private static string? Value(string flag, string[] args)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static MeetingTranscript Sample()
    {
        var lines = new[]
        {
            ("Anna", "Guten Morgen. Heute geht es um den Launch der neuen Version."),
            ("Markus", "Die Tests laufen noch, ich schlage Montag statt Freitag vor."),
            ("Anna", "Einverstanden. Schickst du bis Freitag den Testbericht?"),
            ("Markus", "Ja. Offen ist noch, wer die Release Notes schreibt."),
        };
        var turns = lines.Select((l, i) => new MeetingTranscript.Turn(l.Item1, i * 10, i * 10 + 9, l.Item2));
        return new MeetingTranscript(DateTime.UtcNow, 40, EngineKind.Parakeet, AppLanguage.German, turns, title: "Selbsttest");
    }
}
