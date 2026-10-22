using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LunaCore;

/// <summary>
/// Übersetzt eine Suchanfrage in einen FTS5-Ausdruck.
/// <list type="bullet">
/// <item>Wörter ab 4 Zeichen suchen als Präfix („Preis“ findet „Preise“, „Preiserhöhung“) – hilft bei deutschen
/// Komposita und Beugungen, die der Tokenizer nicht zurückführt.</item>
/// <item>Text in Anführungszeichen ist eine Phrase.</item>
/// <item>Füllwörter („wo“, „haben“, „wir“, „über“) fallen weg: Claude formuliert oft ganze Fragen.</item>
/// <item>Enthält die Anfrage einen Wörterbuch-Begriff oder eine seiner Fehlschreibungen, wird nach allen Varianten
/// gesucht – ältere Transkripte enthalten oft noch die Fehlschreibung.</item>
/// <item><see cref="All"/> verlangt jeden Bestandteil (UND); <see cref="Any"/> ist die Rückfallebene (ODER), wenn UND zu
/// wenig findet.</item>
/// </list>
/// </summary>
public sealed class KeywordQuery
{
    public string? All { get; }
    public string? Any { get; }
    /// <summary>Normalisierte Suchwörter (für Titel- und Sprechernamen-Treffer außerhalb des Index).</summary>
    public IReadOnlyList<string> Terms { get; }

    public bool IsEmpty => All is null;

    public KeywordQuery(string text, IEnumerable<VocabularyEntry>? vocabulary = null)
    {
        var remaining = TextFolding.Fold(text);
        var clauses = new List<string>();
        var terms = new List<string>();

        // 1. Wörterbuch-Varianten (längste zuerst), damit „Luna Transcribe“ vor „Luna“ greift.
        foreach (var group in VariantGroups(vocabulary ?? Enumerable.Empty<VocabularyEntry>()))
        {
            Match? match = null;
            foreach (var variant in group)
            {
                var m = VariantRegex(variant).Match(remaining);
                if (m.Success) { match = m; break; }
            }
            if (match is null) continue;
            remaining = remaining[..match.Index] + " " + remaining[(match.Index + match.Length)..];
            clauses.Add("(" + string.Join(" OR ", group.Select(Phrase)) + ")");
            terms.AddRange(group);
        }

        // 2. Phrasen in Anführungszeichen.
        foreach (var quote in new[] { "„", "“", "”" }) remaining = remaining.Replace(quote, "\"");
        var quoted = remaining.Split('"');
        var bare = new List<string>();
        for (var index = 0; index < quoted.Length; index++)
        {
            if (index % 2 == 0)
            {
                bare.Add(quoted[index]);
                continue;
            }
            var words = Words(quoted[index]);
            if (words.Count == 0) continue;
            var phrase = string.Join(" ", words);
            clauses.Add(Phrase(phrase));
            terms.Add(phrase);
        }

        // 3. Einzelwörter ohne Füllwörter.
        foreach (var word in bare.SelectMany(Words))
        {
            var length = TextUtil.Length(word);
            if (length <= 1 || StopWords.Contains(word)) continue;
            clauses.Add(length >= 4 ? Phrase(word) + "*" : Phrase(word));
            terms.Add(word);
        }

        All = clauses.Count == 0 ? null : string.Join(" AND ", clauses);
        Any = clauses.Count > 1 ? string.Join(" OR ", clauses) : null;
        Terms = terms;
    }

    // MARK: Hilfen

    private static string Phrase(string text) => "\"" + text.Replace("\"", "") + "\"";

    /// <summary>Wörter aus Buchstaben und Ziffern (Grapheme), alles andere trennt.</summary>
    private static List<string> Words(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var grapheme in TextUtil.Characters(text))
        {
            var category = TextUtil.Category(grapheme);
            if (TextUtil.IsLetter(category) || TextUtil.IsNumber(category))
            {
                current.Append(grapheme);
            }
            else if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>Je Wörterbuch-Eintrag alle Schreibweisen (gefaltet), Gruppen mit den längsten Varianten zuerst.</summary>
    private static List<List<string>> VariantGroups(IEnumerable<VocabularyEntry> vocabulary) =>
        vocabulary
            .Select(entry =>
            {
                var unique = new List<string>();
                foreach (var variant in new[] { entry.Term }.Concat(entry.Aliases)
                             .Select(v => string.Join(" ", TextUtil.SplitWhitespace(TextFolding.Fold(v))))
                             .Where(v => v.Length > 0))
                    if (!unique.Contains(variant)) unique.Add(variant);
                return unique.OrderByDescending(TextUtil.Length).ToList();
            })
            .Where(group => group.Count > 0)
            .OrderByDescending(group => TextUtil.Length(group[0]))
            .ToList();

    /// <summary>Fundstelle einer Variante an Wortgrenzen (Leerzeichen flexibel).</summary>
    private static Regex VariantRegex(string variant)
    {
        var pattern = string.Join(@"\s+", variant.Split(' ').Select(Regex.Escape));
        const string wordChar = @"[\p{L}\p{N}_]";
        return new Regex($"(?<!{wordChar}){pattern}(?!{wordChar})", RegexOptions.CultureInvariant);
    }

    /// <summary>Häufige deutsche und englische Wörter ohne Suchwert (bereits gefaltet).</summary>
    internal static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "eines",
        "und", "oder", "aber", "doch", "sondern", "denn", "weil", "dass", "ob", "wenn", "als", "wie",
        "ich", "du", "er", "sie", "es", "wir", "ihr", "mich", "mir", "dich", "dir", "uns", "euch", "ihnen",
        "mein", "dein", "sein", "unser", "euer", "ihre", "ihren", "unsere", "unseren",
        "ist", "sind", "war", "waren", "bin", "bist", "seid", "wird", "werden", "wurde", "wurden",
        "hat", "habe", "haben", "hatte", "hatten", "hast", "kann", "konnen", "konnte", "soll", "sollen",
        "muss", "mussen", "will", "wollen", "gibt", "geben", "gab", "machen", "macht", "gemacht",
        "in", "im", "an", "am", "auf", "aus", "bei", "mit", "nach", "von", "vom", "zu", "zum", "zur",
        "uber", "unter", "vor", "hinter", "neben", "zwischen", "durch", "fur", "gegen", "ohne", "um", "bis", "seit",
        "nicht", "kein", "keine", "auch", "noch", "schon", "nur", "sehr", "mal", "ja", "nein", "so", "da", "dann",
        "was", "wer", "wo", "wann", "warum", "wieso", "welche", "welcher", "welches", "worum", "woruber",
        "alle", "allen", "alles", "etwas", "man", "sich", "hier", "dort", "heute", "gestern",
        "gesagt", "gesprochen", "besprochen", "diskutiert", "meeting", "meetings",
        "the", "a", "and", "or", "of", "to", "on", "for", "with", "about", "is", "are", "was", "were",
        "what", "who", "where", "when", "why", "how", "did", "do", "does", "we", "you", "they", "it", "that", "this",
    };
}
