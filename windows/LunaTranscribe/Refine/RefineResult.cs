using System;

namespace LunaTranscribe.Refine;

/// <summary>Ausgang einer Nachbearbeitung. Alles außer <see cref="Refined"/> heißt: Rohtext einfügen.</summary>
public enum RefineOutcome
{
    Refined,        // bereinigter Text
    Skipped,        // leerer Text – nichts zu tun
    NotReady,       // Sprachmodell lädt noch (oder wird heruntergeladen)
    Failed,         // Modell konnte nicht geladen werden oder Fehler bei der Generierung
    Timeout,        // länger als LlamaRefiner.Timeout bzw. ClaudeRefiner.Timeout
    Unavailable,    // Backend nicht nutzbar (z. B. Claude CLI fehlt oder nicht angemeldet) – Grund steht im Hinweis
    Implausible,    // Antwort leer oder in der Länge unglaubwürdig
    Cancelled,      // vom Aufrufer abgebrochen (z. B. Escape) – ohne Hinweis
}

/// <summary>
/// Ergebnis eines Nachbearbeitungs-Aufrufs. <see cref="Text"/> ist immer einfügbar: der bereinigte Text oder –
/// bei jedem Problem – unverändert der Rohtext. Ein Diktat geht nie verloren.
/// </summary>
/// <param name="Text">Einzufügender Text.</param>
/// <param name="RawText">Ursprüngliches Transkript (für Verlauf/Tooltip).</param>
/// <param name="Notice">Deutscher Hinweis fürs Overlay, wenn der Rohtext eingefügt wird; sonst null.</param>
public sealed record RefineResult(string Text, string RawText, RefineOutcome Outcome, string? Notice, TimeSpan Duration)
{
    public bool IsRefined => Outcome == RefineOutcome.Refined;

    public static RefineResult Success(string refined, string raw, TimeSpan duration) =>
        new(refined, raw, RefineOutcome.Refined, null, duration);

    public static RefineResult Fallback(string raw, RefineOutcome outcome, string? notice, TimeSpan duration) =>
        new(raw, raw, outcome, notice, duration);
}
