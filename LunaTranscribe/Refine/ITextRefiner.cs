using System;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Refine;

/// <summary>
/// Gemeinsame Schnittstelle der Nachbearbeitungs-Backends (unter Windows nur llama.cpp).
/// Zustandslos pro Aufruf: jedes Diktat wird unabhängig vom vorherigen bereinigt.
/// </summary>
public interface ITextRefiner : IDisposable
{
    RefinerKind Kind { get; }
    bool IsLoaded { get; }

    /// <summary>true, wenn die Modelldatei lokal liegt – Laden dauert dann nur Sekunden, kein Download.</summary>
    bool FilesPresent { get; }

    /// <summary>
    /// Lädt das Modell (beim ersten Mal Download von Hugging Face) und wärmt es auf.
    /// <paramref name="progress"/> liegt zwischen 0 und 1 (0–0.9 Download, 0.9–0.95 Laden, 0.95–1 Warm-up).
    /// <paramref name="warmup"/>: Sprache + Anweisung, für die der Prompt-Anfang schon vorberechnet wird (optional).
    /// </summary>
    Task PrepareAsync(Action<double> progress, RefineWarmup? warmup, CancellationToken ct);

    /// <summary>Berechnet den Prompt-Anfang für eine (neue) Sprache/Anweisung vor, z. B. nach Änderung in den Einstellungen.</summary>
    Task PrimeAsync(RefineWarmup warmup, CancellationToken ct);

    /// <summary>
    /// Gibt den bereinigten Text zurück. Wirft <see cref="RefinerException"/> (nicht geladen, Timeout,
    /// unplausible Antwort, Fehler) – der Aufrufer fällt dann auf den Rohtext zurück.
    /// </summary>
    /// <param name="promptTemplate">System-Anweisung mit <c>{language}</c>-Platzhalter; null/leer = Standard.</param>
    Task<string> RefineAsync(string text, AppLanguage language, string? promptTemplate, CancellationToken ct);

    void Unload();
}

/// <summary>Für welche Sprache/Anweisung der Prompt-Anfang beim Laden vorberechnet wird (meist die Hauptsprache).</summary>
public sealed record RefineWarmup(AppLanguage Language, string? PromptTemplate);

public sealed class RefinerException : Exception
{
    public RefineOutcome Outcome { get; }

    public RefinerException(RefineOutcome outcome, string message, Exception? inner = null) : base(message, inner) =>
        Outcome = outcome;

    public static RefinerException NotLoaded() => new(RefineOutcome.NotReady, "Das Sprachmodell ist noch nicht geladen.");

    public static RefinerException Timeout() => new(RefineOutcome.Timeout, "Die Nachbearbeitung hat zu lange gedauert.");

    public static RefinerException ImplausibleOutput() =>
        new(RefineOutcome.Implausible, "Das Sprachmodell hat keinen brauchbaren Text geliefert.");

    public static RefinerException Failed(string message, Exception? inner = null) => new(RefineOutcome.Failed, message, inner);
}
