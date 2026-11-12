using System;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Output;
using LunaTranscribe.Summary;

namespace LunaTranscribe.Refine;

/// <summary>
/// Nachbearbeitung mit Claude Haiku über die lokal installierte Claude CLI (<c>claude -p</c>) – mit dem Claude-Konto des
/// Nutzers, ohne API-Schlüssel. Niedriger Aufwand (<c>--effort low</c>), weil das Bereinigen eines Diktats kein Nachdenken
/// braucht und jede Sekunde vor dem Einfügen zählt.
///
/// Achtung Local-First: Das Diktat verlässt den PC und geht an Anthropic. Die Einstellungen weisen darauf hin;
/// Rohtext und Audio bleiben wie immer lokal.
/// </summary>
public sealed class ClaudeRefiner : ITextRefiner
{
    /// <summary>
    /// Explizite Modell-ID statt des Alias <c>haiku</c>: Ältere CLI-Versionen lösen den Alias noch auf Haiku 4.5 auf, das
    /// im Test (2026-11-02, macOS) 10–75 s brauchte; Haiku 5.5 antwortet in 0,6–2,3 s. Kennt die CLI die ID nicht, warnt
    /// sie nur auf stderr und reicht sie trotzdem durch.
    /// </summary>
    public const string Model = "claude-haiku-5-5";
    public const string Effort = "low";

    /// <summary>Großzügiger als bei llama.cpp: Die CLI startet bei jedem Diktat neu und braucht das Netz.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _customPath;
    private volatile ClaudeCli? _cli;

    /// <param name="customPath">Eingestellter Pfad zur CLI (Einstellungen → Meetings); "" = automatisch suchen.</param>
    public ClaudeRefiner(Func<string?> customPath) => _customPath = customPath;

    public RefinerKind Kind => RefinerKind.ClaudeHaiku;

    public bool IsLoaded => _cli != null;

    /// <summary>Kein Download – „Laden“ heißt nur, die CLI zu finden.</summary>
    public bool FilesPresent => true;

    /// <summary>
    /// Nichts zu laden – nur die CLI finden und prüfen, dass sie startet. Die Anmeldung zeigt sich erst beim ersten Diktat
    /// (eine Prüfung kostete selbst einen Modellaufruf).
    /// </summary>
    public async Task PrepareAsync(Action<double> progress, RefineWarmup? warmup, CancellationToken ct)
    {
        if (_cli != null) return;
        progress(0.3);
        // Locate fragt im Zweifel „where claude“ – nicht auf dem UI-Thread.
        var found = await Task.Run(() => ClaudeCli.Locate(_customPath()), ct)
                    ?? throw new RefinerException(RefineOutcome.Unavailable, ClaudeCliException.NotFound().Message);
        progress(0.6);
        string version;
        try { version = await found.VersionAsync(ct); }
        catch (ClaudeCliException ex) { throw new RefinerException(RefineOutcome.Unavailable, ex.Message, ex); }
        ct.ThrowIfCancellationRequested();
        _cli = found;
        progress(1.0);
        Log.Write($"Claude CLI ready: {version.Trim()} ({found.Executable})");
    }

    /// <summary>Kein Prompt-Cache – jeder Aufruf schickt die ganze Anweisung.</summary>
    public Task PrimeAsync(RefineWarmup warmup, CancellationToken ct) => Task.CompletedTask;

    public async Task<string> RefineAsync(string text, AppLanguage language, string? promptTemplate, CancellationToken ct)
    {
        var cli = _cli ?? throw RefinerException.NotLoaded();

        // Wie Apple Intelligence in der macOS-Version: Beispielpaare als Text in der Anweisung – Haiku ahmt das Format nicht nach.
        var system = RefinePrompt.Instructions(language, promptTemplate);
        foreach (var (input, output) in RefinePrompt.Examples(language))
            system += $"\nExample dictation: {input}\nExample result: {output}";

        string raw;
        try
        {
            raw = await cli.CompleteAsync(system, RefinePrompt.UserMessage(text), Model, Effort, Timeout, ct);
        }
        catch (ClaudeCliException ex) when (ex.Kind == ClaudeCliErrorKind.TimedOut)
        {
            throw RefinerException.Timeout();
        }
        catch (ClaudeCliException ex) when (ex.IsNotLoggedIn)
        {
            // Kurz halten – der Grund erscheint im Overlay.
            throw new RefinerException(RefineOutcome.Unavailable, "Claude CLI nicht angemeldet (im Terminal „claude“ starten)", ex);
        }
        catch (ClaudeCliException ex)
        {
            throw new RefinerException(RefineOutcome.Unavailable, ex.Message, ex);
        }

        return RefinePrompt.Sanitize(raw, text) ?? throw RefinerException.ImplausibleOutput();
    }

    public void Unload() => _cli = null;

    public void Dispose() => _cli = null;
}
