using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

/// <summary>
/// Stand des lernenden Wörterbuchs, wie ihn die App speichert: Einträge, offene Vorschläge, dauerhaft ignorierte
/// Vorschlagsschlüssel (<see cref="VocabularySuggestion.Key"/>).
/// </summary>
public sealed record VocabularyState(
    IReadOnlyList<VocabularyEntry> Entries,
    IReadOnlyList<VocabularySuggestion> Suggestions,
    IReadOnlySet<string> IgnoredKeys)
{
    public static readonly VocabularyState Empty =
        new(Array.Empty<VocabularyEntry>(), Array.Empty<VocabularySuggestion>(), new HashSet<string>());

    public int PendingCount => Suggestions.Count;
}

/// <summary>
/// Das lernende Wörterbuch als zustandslose Logik: macht aus Korrekturen im Verlauf und in Meeting-Transkripten
/// Vorschläge und übernimmt sie auf Wunsch. Jede Funktion liefert einen neuen <see cref="VocabularyState"/>, den die
/// App speichert; die Eingaben bleiben unverändert. Nichts wird automatisch aktiv – eine falsche Ersetzung wäre in
/// jedem künftigen Diktat, eine fehlende nur ein Tippfehler.
/// </summary>
public static class VocabularyLearner
{
    /// <summary>
    /// Vergleicht Original und Korrektur und ergänzt neue Vorschläge. <c>Changed</c> = Anzahl neuer oder bestätigter
    /// Vorschläge (für einen Hinweis in der UI); bei 0 ist der Zustand unverändert.
    /// </summary>
    public static (VocabularyState State, int Changed) Learn(VocabularyState state, string before, string after, DateTime? now = null)
    {
        var timestamp = now ?? DateTime.UtcNow;
        var known = new CustomVocabulary(state.Entries);
        var suggestions = state.Suggestions.ToList();
        int changed = 0;

        foreach (var candidate in CorrectionLearner.Candidates(before, after))
        {
            var key = VocabularySuggestion.KeyFor(candidate.Term, candidate.Variant);
            if (state.IgnoredKeys.Contains(key) || known.Correct(candidate.Variant) == candidate.Term)
                continue;   // ignoriert oder kennt das Wörterbuch schon

            var index = suggestions.FindIndex(s => s.Key == key);
            if (index >= 0)
                suggestions[index] = suggestions[index] with { Count = suggestions[index].Count + 1, LastSeen = timestamp };
            else
                suggestions.Add(new VocabularySuggestion(candidate.Term, candidate.Variant, timestamp));
            changed++;
        }
        if (changed == 0) return (state, 0);

        var sorted = suggestions.OrderByDescending(s => s.Count).ThenByDescending(s => s.LastSeen).ToList();
        return (state with { Suggestions = sorted }, changed);
    }

    /// <summary>Übernimmt einen Vorschlag: als Variante eines vorhandenen Begriffs oder als neuer Eintrag.</summary>
    public static VocabularyState Accept(VocabularyState state, VocabularySuggestion suggestion)
    {
        var entries = state.Entries.Select(e => e.Clone()).ToList();
        var existing = entries.FirstOrDefault(e => string.Equals(e.Term, suggestion.Term, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.Aliases.Any(a => string.Equals(a, suggestion.Variant, StringComparison.OrdinalIgnoreCase)))
                existing.Aliases.Add(suggestion.Variant);
        }
        else
        {
            entries.Add(new VocabularyEntry(suggestion.Term, new[] { suggestion.Variant }));
        }
        return Remove(state with { Entries = entries }, suggestion);
    }

    /// <summary>Verwirft einen Vorschlag dauerhaft.</summary>
    public static VocabularyState Ignore(VocabularyState state, VocabularySuggestion suggestion)
    {
        var ignored = new HashSet<string>(state.IgnoredKeys) { suggestion.Key };
        return Remove(state with { IgnoredKeys = ignored }, suggestion);
    }

    /// <summary>Ersetzt einen Vorschlag (gleiche <see cref="VocabularySuggestion.Id"/>), z. B. nach Bearbeiten des Begriffs.</summary>
    public static VocabularyState Update(VocabularyState state, VocabularySuggestion suggestion)
    {
        var suggestions = state.Suggestions.ToList();
        var index = suggestions.FindIndex(s => s.Id == suggestion.Id);
        if (index < 0) return state;
        suggestions[index] = suggestion;
        return state with { Suggestions = suggestions };
    }

    private static VocabularyState Remove(VocabularyState state, VocabularySuggestion suggestion) =>
        state with { Suggestions = state.Suggestions.Where(s => s.Id != suggestion.Id).ToList() };
}
