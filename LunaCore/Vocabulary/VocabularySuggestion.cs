using System;

namespace LunaCore;

/// <summary>
/// Ein aus einer Korrektur gelernter Wörterbuch-Vorschlag: <see cref="Variant"/> wurde erkannt, <see cref="Term"/>
/// war gemeint. Wird erst aktiv, wenn der Nutzer ihn übernimmt – falsche Ersetzungen sind schlimmer als fehlende.
/// Ohne JSON-Attribute: Die App speichert ihn in settings.json (PascalCase).
/// </summary>
public sealed record VocabularySuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Richtige Schreibweise („weclapp“).</summary>
    public string Term { get; set; } = "";
    /// <summary>So hat das Modell den Begriff erkannt („Weglap“).</summary>
    public string Variant { get; set; } = "";
    /// <summary>Wie oft dieselbe Korrektur gesehen wurde; sortiert die Liste.</summary>
    public int Count { get; set; } = 1;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public VocabularySuggestion() { }

    public VocabularySuggestion(string term, string variant, DateTime? lastSeen = null)
    {
        Term = term;
        Variant = variant;
        LastSeen = lastSeen ?? DateTime.UtcNow;
    }

    /// <summary>Identität unabhängig von Groß-/Kleinschreibung – dient auch der Ignorierliste.</summary>
    public string Key => KeyFor(Term, Variant);

    public static string KeyFor(string term, string variant) => $"{variant.ToLowerInvariant()}→{term.ToLowerInvariant()}";

    /// <summary>„1 neuer Wörterbuch-Vorschlag“ / „3 neue Wörterbuch-Vorschläge“</summary>
    public static string CountLabel(int count) =>
        count == 1 ? "1 neuer Wörterbuch-Vorschlag" : $"{count} neue Wörterbuch-Vorschläge";
}
