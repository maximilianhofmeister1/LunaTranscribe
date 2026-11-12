using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Refine;

/// <summary>
/// Entwickler-Einstieg ohne Sprechen, um Prompt und Modell der Nachbearbeitung reproduzierbar zu prüfen:
/// <code>
/// LunaTranscribe.exe --refine-selftest "ähm also ich wollte sagen dass dass wir morgen um elf uhr das meeting haben" --language de
/// </code>
/// Optional <c>--model lfm2|qwen3_1_7b|qwen3_4b|claudeHaiku</c> (Standard: LFM2), <c>--language de|en|…</c> (Standard: de) und
/// <c>--prompt &lt;datei&gt;</c> (Anweisung mit <c>{language}</c>, Standard: eingebaute) zum Vergleichen von Prompt-Varianten.
/// Lädt das Modell (beim ersten Mal mit Download), bereinigt den Text dreimal (erster Lauf = kalt, weitere = warm)
/// und druckt Ergebnis und Dauer.
/// </summary>
internal static class RefineSelfTest
{
    [SelfTest("--refine-selftest")]
    private static async Task<int> Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* keine Konsole */ }

        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            SelfTests.Print("Aufruf: --refine-selftest \"<text>\" [--model lfm2|qwen3_1_7b|qwen3_4b|claudeHaiku] [--language de] [--prompt <datei>]");
            return 2;
        }
        var text = args[0];
        var kind = RefinerKind.Lfm2_1_2B;
        var language = AppLanguage.German;
        string? template = null;
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--model":
                    if (!RefinerKindExtensions.TryParseKey(args[i + 1], out kind))
                    {
                        SelfTests.Print($"Unbekanntes Modell: {args[i + 1]}");
                        return 2;
                    }
                    break;
                case "--language":
                    if (AppLanguageExtensions.FromCode(args[i + 1]) is not { } l)
                    {
                        SelfTests.Print($"Unbekannte Sprache: {args[i + 1]}");
                        return 2;
                    }
                    language = l;
                    break;
                case "--prompt":
                    template = await File.ReadAllTextAsync(args[i + 1], Encoding.UTF8);
                    break;
            }
        }

        var source = kind.IsCloud() ? $"Claude CLI, {ClaudeRefiner.Model}, --effort {ClaudeRefiner.Effort}" : $"{kind.Model().HuggingFaceRepo}/{kind.Model().Files[0]}";
        SelfTests.Print($"Modell: {kind.ShortLabel()} ({source}), Sprache: {language.DisplayName()}");
        if (template != null) SelfTests.Print($"Anweisung aus Datei ({template.Length} Zeichen)");

        var manager = new RefinerManager(Dispatcher.CurrentDispatcher) { ClaudeCliPath = () => SettingsStore.Shared.ClaudeCliPath };
        try
        {
            // Wie in der App: Prompt-Anfang (Anweisung + Beispiele) wird beim Laden vorberechnet; „kalt“ = erstes Diktat danach.
            manager.EnsureLoaded(kind, new RefineWarmup(language, template));
            var t0 = Stopwatch.StartNew();
            var lastPrinted = -1;
            while (!manager.IsReady)
            {
                if (manager.State.Kind == ModelStateKind.Failed)
                {
                    SelfTests.Print("FEHLER: " + manager.State.Error);
                    return 1;
                }
                var percent = (int)(manager.State.Progress * 100);
                if (!manager.FilesPresent && percent / 10 != lastPrinted / 10)
                {
                    lastPrinted = percent;
                    SelfTests.Print("  " + manager.StatusText);
                }
                await Task.Delay(200);
            }
            SelfTests.Print($"Geladen in {t0.Elapsed.TotalSeconds:0.0} s (inkl. Vorberechnung des Prompt-Anfangs)");
            using (var process = Process.GetCurrentProcess())
                SelfTests.Print($"Arbeitsspeicher: {process.PrivateMemorySize64 / 1e9:0.00} GB privat, {process.WorkingSet64 / 1e9:0.00} GB Working Set");

            SelfTests.Print("");
            SelfTests.Print($"Eingabe:  {text}");
            var failures = 0;
            for (var pass = 1; pass <= 3; pass++)
            {
                var result = await manager.RefineAsync(text, language, kind, template);
                var label = pass == 1 ? "kalt" : "warm";
                if (result.IsRefined)
                    SelfTests.Print($"Ausgabe ({label}, {result.Duration.TotalSeconds:0.00} s): {result.Text}");
                else
                {
                    failures++;
                    SelfTests.Print($"Rohtext ({label}, {result.Duration.TotalSeconds:0.00} s, {result.Outcome}): {result.Notice}");
                }
                if (result.IsRefined && manager.Current is LlamaRefiner { LastStats: { } stats }) SelfTests.Print("  " + stats);
            }
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            manager.Shutdown();
        }
    }
}
