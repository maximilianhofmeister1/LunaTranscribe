using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LunaCore;

/// <summary>
/// Erkennt in einer Nutzerkorrektur („vorher“ → „nachher“) Verhörer, die ins Wörterbuch gehören.
/// Vorgehen: Wort-Diff über die längste gemeinsame Teilfolge; jede Stelle, an der 1–3 Wörter durch 1–3 andere
/// ersetzt wurden, ist ein Kandidat. Verworfen wird, was nach Umformulierung statt Verhörer aussieht: Füllwörter,
/// reine Satzanfang-Großschreibung, Beugungen, zu unähnliche Schreibweisen. Rein und ohne Zustand.
/// </summary>
public static class CorrectionLearner
{
    /// <param name="Variant">So stand es da (Erkennung).</param>
    /// <param name="Term">So hat der Nutzer es korrigiert.</param>
    public readonly record struct Candidate(string Variant, string Term);

    public const int MaxWords = 3;
    /// <summary>
    /// Anteil unveränderter Wörter, ab dem eine Änderung als Korrektur gilt. Darunter wurde der Text umgeschrieben –
    /// dann sind auch zufällig ähnliche Wörter keine Verhörer.
    /// </summary>
    public const double MinKeptRatio = 0.5;
    /// <summary>Mindestähnlichkeit (0…1) der Schreibweise oder des Klangs, damit es als Verhörer zählt.</summary>
    public const double MinSimilarity = 0.5;

    public static List<Candidate> Candidates(string before, string after)
    {
        var old = Tokens(before);
        var @new = Tokens(after);
        if (old.Count == 0 || @new.Count == 0) return new();
        var oldWords = old.Select(t => t.Word).ToList();
        var newWords = @new.Select(t => t.Word).ToList();
        var kept = CommonWordCount(oldWords, newWords);
        if ((double)kept / Math.Max(old.Count, @new.Count) < MinKeptRatio) return new();

        var result = new List<Candidate>();
        foreach (var hunk in Replacements(oldWords, newWords))
        {
            var variant = string.Join(" ", hunk.Removed);
            var term = string.Join(" ", hunk.Inserted);
            var atSentenceStart = hunk.OldIndex == 0 || old[hunk.OldIndex - 1].EndsSentence;
            var candidate = new Candidate(variant, term);
            if (IsLikelyMishearing(variant, term, atSentenceStart) && !result.Contains(candidate))
                result.Add(candidate);
        }
        return result;
    }

    // MARK: Filter

    public static bool IsLikelyMishearing(string variant, string term, bool atSentenceStart)
    {
        if (variant == term || TextUtil.Length(term) < 2 || TextUtil.Length(variant) < 2) return false;
        // Zahlen, Zeichen: ohne Buchstaben kein Begriff.
        if (!term.EnumerateRunes().Any(r => TextUtil.IsLetter(System.Text.Rune.GetUnicodeCategory(r))
                                            || TextUtil.IsMark(System.Text.Rune.GetUnicodeCategory(r)))) return false;

        var variantWords = variant.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var termWords = term.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (termWords.All(Stopwords.Contains) || variantWords.All(Stopwords.Contains)) return false;

        // Nur die Groß-/Kleinschreibung geändert: am Satzanfang ist das Grammatik, sonst eine Marke („weclapp“, „iPhone“).
        if (variant.ToLowerInvariant() == term.ToLowerInvariant()) return !atSentenceStart;
        if (IsInflection(variant, term)) return false;

        var spelling = Similarity(Compact(variant), Compact(term));
        var sound = Similarity(PhoneticKey(variant), PhoneticKey(term));
        return Math.Max(spelling, sound) >= MinSimilarity;
    }

    /// <summary>Häufige Funktionswörter (de/en): deren Austausch ist Umformulierung, kein Verhörer.</summary>
    public static readonly IReadOnlySet<string> Stopwords = new HashSet<string>
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "aber",
        "ich", "du", "er", "sie", "es", "wir", "ihr", "mit", "von", "zu", "zum", "zur", "im", "in", "an", "am", "auf",
        "für", "ist", "sind", "war", "hat", "haben", "nicht", "noch", "auch", "so", "dass", "da", "wie", "was", "wo",
        "the", "a", "an", "and", "or", "but", "i", "you", "he", "she", "it", "we", "they", "to", "of", "in", "on", "at",
        "for", "is", "are", "was", "be", "not", "that", "this", "with", "as", "so",
    };

    /// <summary>
    /// Grammatik statt Verhörer: ein normales, kleingeschriebenes Wort mit gleichem Stamm und anderer Endung
    /// („klappen“ → „klappten“, „gehe“ → „gehen“). Namen und Marken tragen fast immer Großbuchstaben.
    /// </summary>
    public static bool IsInflection(string variant, string term)
    {
        if (variant.Contains(' ') || term.Contains(' ')
            || variant != variant.ToLowerInvariant() || term != term.ToLowerInvariant()) return false;
        var a = TextUtil.Characters(variant);
        var b = TextUtil.Characters(term);
        int shared = 0;
        while (shared < a.Count && shared < b.Count && a[shared] == b[shared]) shared++;
        return shared >= 4 && shared >= 0.6 * Math.Min(a.Count, b.Count);
    }

    // MARK: Ähnlichkeit

    /// <summary>Normalisierte Levenshtein-Ähnlichkeit: 1 = gleich, 0 = völlig verschieden.</summary>
    public static double Similarity(string a, string b)
    {
        var x = TextUtil.Characters(a);
        var y = TextUtil.Characters(b);
        if (x.Count == 0) return y.Count == 0 ? 1 : 0;
        if (y.Count == 0) return 0;
        var previous = Enumerable.Range(0, y.Count + 1).ToArray();
        for (int i = 1; i <= x.Count; i++)
        {
            var current = new int[y.Count + 1];
            current[0] = i;
            for (int j = 1; j <= y.Count; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                                      previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1 - (double)previous[y.Count] / Math.Max(x.Count, y.Count);
    }

    private static readonly (string From, string To)[] PhoneticRules =
    {
        ("sch", "s"), ("ph", "f"), ("th", "t"), ("ck", "k"), ("qu", "kv"), ("dt", "t"),
        ("ie", "i"), ("ei", "ai"), ("ey", "ai"), ("tz", "z"), ("ä", "e"), ("ö", "o"), ("ü", "u"),
        ("w", "v"), ("c", "k"), ("g", "k"), ("b", "p"), ("d", "t"), ("z", "s"), ("y", "i"),
    };

    /// <summary>Grobe Klangform für Deutsch/Englisch: gleiche Laute, verschiedene Schreibweisen („Weglap“ ≈ „weclapp“).</summary>
    public static string PhoneticKey(string text)
    {
        var s = Compact(text);
        foreach (var (from, to) in PhoneticRules) s = s.Replace(from, to, StringComparison.Ordinal);
        var result = new List<string>();
        foreach (var ch in TextUtil.Characters(s))
            if (ch != "h" && (result.Count == 0 || result[^1] != ch)) result.Add(ch);
        return string.Concat(result);
    }

    /// <summary>Klein, ohne Leer- und Satzzeichen.</summary>
    private static string Compact(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in TextUtil.Characters(text.ToLowerInvariant()))
        {
            var cat = TextUtil.Category(ch);
            if (TextUtil.IsLetter(cat) || TextUtil.IsNumber(cat)) sb.Append(ch);
        }
        return sb.ToString();
    }

    // MARK: Diff

    /// <param name="Word">Ohne Satzzeichen am Rand – verglichen wird nur das Wort.</param>
    public readonly record struct Token(string Word, bool EndsSentence);

    public static List<Token> Tokens(string text)
    {
        var result = new List<Token>();
        foreach (var raw in TextUtil.SplitWhitespace(text))
        {
            var word = TrimPunctuation(raw);
            if (word.Length == 0) continue;
            var last = raw[^1];
            result.Add(new Token(word, last is '.' or '!' or '?' or ':'));
        }
        return result;
    }

    /// <summary>Entfernt Satz- und Sonderzeichen am Rand, außer „+“ und „#“ („C++“, „C#“).</summary>
    private static string TrimPunctuation(string raw)
    {
        static bool Trim(System.Text.Rune r) =>
            r.Value != '+' && r.Value != '#' && TextUtil.IsPunctuationOrSymbol(System.Text.Rune.GetUnicodeCategory(r));

        int start = 0, end = raw.Length;
        while (start < end && System.Text.Rune.DecodeFromUtf16(raw.AsSpan(start, end - start), out var r, out var n)
               == System.Buffers.OperationStatus.Done && Trim(r)) start += n;
        while (end > start && System.Text.Rune.DecodeLastFromUtf16(raw.AsSpan(start, end - start), out var r, out var n)
               == System.Buffers.OperationStatus.Done && Trim(r)) end -= n;
        return raw[start..end];
    }

    /// <param name="OldIndex">Position des ersten ersetzten Worts im Originaltext.</param>
    public sealed record Hunk(int OldIndex, IReadOnlyList<string> Removed, IReadOnlyList<string> Inserted);

    /// <summary>LCS-Tabelle von hinten: <c>lcs[i, j]</c> = gemeinsame Wörter von <c>a[i...]</c> und <c>b[j...]</c>.</summary>
    private static int[,] LcsTable(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        return lcs;
    }

    public static int CommonWordCount(IReadOnlyList<string> a, IReadOnlyList<string> b) => LcsTable(a, b)[0, 0];

    /// <summary>Ersetzungen (nicht reine Einfügungen/Löschungen) aus dem LCS-Diff zweier Wortfolgen.</summary>
    public static List<Hunk> Replacements(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var lcs = LcsTable(a, b);
        var hunks = new List<Hunk>();
        int i = 0, j = 0, start = 0;
        var removed = new List<string>();
        var inserted = new List<string>();

        void BeginHunkIfNeeded()
        {
            if (removed.Count == 0 && inserted.Count == 0) start = i;
        }

        void Flush()
        {
            if (removed.Count > 0 && inserted.Count > 0 && removed.Count <= MaxWords && inserted.Count <= MaxWords)
                hunks.Add(new Hunk(start, removed.ToList(), inserted.ToList()));
            removed.Clear();
            inserted.Clear();
        }

        while (i < a.Count || j < b.Count)
        {
            if (i < a.Count && j < b.Count && a[i] == b[j])
            {
                Flush();
                i++;
                j++;
            }
            else if (j < b.Count && (i == a.Count || lcs[i, j + 1] >= lcs[i + 1, j]))
            {
                BeginHunkIfNeeded();
                inserted.Add(b[j]);
                j++;
            }
            else
            {
                BeginHunkIfNeeded();
                removed.Add(a[i]);
                i++;
            }
        }
        Flush();
        return hunks;
    }
}
