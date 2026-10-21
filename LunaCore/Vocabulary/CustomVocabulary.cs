using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LunaCore;

/// <summary>
/// Das aktive Wörterbuch, so wie es eine Engine anwendet. Unveränderlich und threadsicher: Der Regex wird einmal
/// beim Erzeugen gebaut, <see cref="Correct"/> läuft auch auf Hintergrund-Tasks (Meeting).
/// <see cref="Correct"/> ersetzt jede Variante (und den Begriff in falscher Groß-/Kleinschreibung) deterministisch
/// durch den Begriff – für jede Engine und jede Sprache.
/// </summary>
public sealed class CustomVocabulary : IEquatable<CustomVocabulary>
{
    /// <summary>Bereinigte Einträge: Begriff nicht leer, Varianten getrimmt und ohne Leereinträge.</summary>
    public IReadOnlyList<VocabularyEntry> Entries { get; }

    /// <summary>Findet alle Varianten in einem Durchlauf; null bei leerem Wörterbuch.</summary>
    private readonly Regex? _pattern;
    /// <summary>Normalisierte Variante (klein, einfache Leerzeichen) → Begriff.</summary>
    private readonly Dictionary<string, string> _replacements;

    public static readonly CustomVocabulary Disabled = new(Array.Empty<VocabularyEntry>());

    public bool IsEmpty => Entries.Count == 0;

    public CustomVocabulary(IEnumerable<VocabularyEntry> entries)
    {
        var cleaned = new List<VocabularyEntry>();
        foreach (var entry in entries)
        {
            var term = entry.Term.Trim();
            if (term.Length == 0) continue;
            var aliases = entry.Aliases.Select(a => a.Trim()).Where(a => a.Length > 0);
            cleaned.Add(new VocabularyEntry(term, aliases, entry.Id));
        }
        Entries = cleaned;

        // Der Begriff selbst zählt als Variante, damit auch „kubernetes“ → „Kubernetes“ korrigiert wird.
        // Bei doppelten Varianten gewinnt der erste Eintrag.
        _replacements = new Dictionary<string, string>();
        foreach (var entry in cleaned)
            foreach (var variant in entry.Aliases.Prepend(entry.Term))
                _replacements.TryAdd(Normalize(variant), entry.Term);
        _pattern = MakePattern(_replacements.Keys);
    }

    /// <summary>
    /// Ersetzt alle bekannten Varianten durch die richtige Schreibweise. Groß-/Kleinschreibung und die Anzahl der
    /// Leerzeichen zwischen Wörtern spielen keine Rolle; ersetzt wird nur an Wortgrenzen (Unicode-fähig, also auch
    /// bei Umlauten), nie mitten in einem Wort.
    /// </summary>
    public string Correct(string text)
    {
        if (_pattern is null) return text;
        return _pattern.Replace(text, m => _replacements.TryGetValue(Normalize(m.Value), out var term) ? term : m.Value);
    }

    public bool Equals(CustomVocabulary? other) => other is not null && Entries.SequenceEqual(other.Entries);

    public override bool Equals(object? obj) => Equals(obj as CustomVocabulary);

    public override int GetHashCode() => Entries.Count;

    // MARK: Hilfen

    private static string Normalize(string text) => string.Join(" ", TextUtil.SplitWhitespace(text.ToLowerInvariant()));

    /// <summary>
    /// Eine Alternation über alle Varianten, längste zuerst: So gewinnt „Luna Transcribe“ vor „Luna“, und ein bereits
    /// ersetzter Begriff wird nie ein zweites Mal ersetzt.
    /// </summary>
    private static Regex? MakePattern(IEnumerable<string> variants)
    {
        var list = variants.ToList();
        if (list.Count == 0) return null;
        var alternatives = string.Join("|", list
            .OrderByDescending(TextUtil.Length)
            .Select(v => string.Join(@"\s+", v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))));
        // Wortgrenze über Lookarounds statt \b: \b versagt bei Varianten, die mit einem Sonderzeichen beginnen
        // oder enden („C++“, „.NET“).
        const string wordChar = @"[\p{L}\p{N}_]";
        return new Regex($"(?<!{wordChar})(?:{alternatives})(?!{wordChar})",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
