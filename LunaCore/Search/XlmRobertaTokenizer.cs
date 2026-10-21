using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LunaCore;

/// <summary>
/// Tokenizer von XLM-RoBERTa (multilingual-e5-small) aus der <c>tokenizer.json</c> von Hugging Face – nachgebaut wie in
/// der Rust-Bibliothek <c>tokenizers</c>, damit die Token-IDs exakt passen:
/// <list type="number">
/// <item>Normalisierung „Precompiled“ (SentencePiece-Tabelle <c>nmt_nfkc</c>, als Double-Array-Trie gespeichert),</item>
/// <item>mehrere Leerzeichen → eins,</item>
/// <item>Metaspace: Leerzeichen → „▁“, „▁“ vorn, Teilung vor jedem „▁“,</item>
/// <item>Unigram-Modell: wahrscheinlichste Zerlegung je Wort (Viterbi), unbekannte Zeichen → &lt;unk&gt;,</item>
/// <item>&lt;s&gt; … &lt;/s&gt; drumherum, auf <c>maxTokens</c> gekürzt.</item>
/// </list>
/// Rein verwaltet (kein natives Paket), daher im Kern und mit <c>dotnet test</c> prüfbar.
/// </summary>
public sealed class XlmRobertaTokenizer
{
    private const char Replacement = '▁';
    /// <summary>Abzug für unbekannte Zeichen gegenüber dem schlechtesten Vokabel-Score (wie in <c>tokenizers</c>).</summary>
    private const double UnkPenalty = 10.0;

    private readonly Dictionary<string, (int Id, double Score)> _pieces;
    private readonly int _maxPieceLength;
    private readonly double _unkScore;
    private readonly PrecompiledCharsMap? _charsMap;
    private readonly List<(Regex Pattern, string Content)> _replacements;

    public int UnkId { get; }
    public int BosId { get; }
    public int EosId { get; }
    public int PadId { get; }

    private XlmRobertaTokenizer(Dictionary<string, (int, double)> pieces, int unkId, int bosId, int eosId, int padId,
                                PrecompiledCharsMap? charsMap, List<(Regex, string)> replacements)
    {
        _pieces = pieces;
        _maxPieceLength = pieces.Keys.Count == 0 ? 1 : pieces.Keys.Max(k => k.Length);
        _unkScore = (pieces.Count == 0 ? 0 : pieces.Values.Min(v => v.Item2)) - UnkPenalty;
        _charsMap = charsMap;
        _replacements = replacements;
        UnkId = unkId;
        BosId = bosId;
        EosId = eosId;
        PadId = padId;
    }

    public static XlmRobertaTokenizer Load(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
        return FromJson(doc.RootElement);
    }

    public static XlmRobertaTokenizer FromJson(JsonElement root)
    {
        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram")
            throw new InvalidDataException("tokenizer.json: erwartet ein Unigram-Modell.");

        var specials = new Dictionary<string, int>();
        if (root.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
            foreach (var token in added.EnumerateArray())
                if (token.TryGetProperty("special", out var special) && special.ValueKind == JsonValueKind.True)
                    specials[token.GetProperty("content").GetString()!] = token.GetProperty("id").GetInt32();

        var pieces = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
        var id = 0;
        foreach (var entry in model.GetProperty("vocab").EnumerateArray())
        {
            var piece = entry[0].GetString()!;
            var score = entry[1].GetDouble();
            // Spezial-Token trennt die Bibliothek vor der Zerlegung ab; im Gitter kommen sie nicht vor.
            if (!specials.ContainsKey(piece)) pieces.TryAdd(piece, (id, score));
            id++;
        }
        var unkId = model.TryGetProperty("unk_id", out var unk) && unk.ValueKind == JsonValueKind.Number ? unk.GetInt32() : 3;

        PrecompiledCharsMap? charsMap = null;
        var replacements = new List<(Regex, string)>();
        if (root.TryGetProperty("normalizer", out var normalizer) && normalizer.ValueKind == JsonValueKind.Object)
        {
            var list = normalizer.GetProperty("type").GetString() == "Sequence"
                ? normalizer.GetProperty("normalizers").EnumerateArray().ToList()
                : new List<JsonElement> { normalizer };
            foreach (var n in list)
            {
                switch (n.GetProperty("type").GetString())
                {
                    case "Precompiled":
                        var map = n.GetProperty("precompiled_charsmap").GetString();
                        if (!string.IsNullOrEmpty(map)) charsMap = new PrecompiledCharsMap(Convert.FromBase64String(map));
                        break;
                    case "Replace":
                        var pattern = n.GetProperty("pattern");
                        var regex = pattern.TryGetProperty("Regex", out var r)
                            ? new Regex(r.GetString()!, RegexOptions.CultureInvariant)
                            : new Regex(Regex.Escape(pattern.GetProperty("String").GetString()!), RegexOptions.CultureInvariant);
                        replacements.Add((regex, n.GetProperty("content").GetString() ?? ""));
                        break;
                }
            }
        }

        int Special(string name, int fallback) => specials.TryGetValue(name, out var value) ? value : fallback;
        return new XlmRobertaTokenizer(pieces, unkId, Special("<s>", 0), Special("</s>", 2), Special("<pad>", 1), charsMap, replacements);
    }

    /// <summary>Token-IDs mit &lt;s&gt; und &lt;/s&gt;, höchstens <paramref name="maxTokens"/> insgesamt.</summary>
    public int[] Encode(string text, int maxTokens = 512)
    {
        var ids = new List<int> { BosId };
        foreach (var word in PreTokenize(Normalize(text)))
        {
            ids.AddRange(EncodeWord(word));
            if (ids.Count >= maxTokens - 1) break;
        }
        if (ids.Count > maxTokens - 1) ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        ids.Add(EosId);
        return ids.ToArray();
    }

    /// <summary>Die Wortstücke (ohne Spezial-Token) – für Tests und Fehlersuche.</summary>
    public List<string> Tokenize(string text)
    {
        var result = new List<string>();
        foreach (var word in PreTokenize(Normalize(text))) result.AddRange(Viterbi(word).Select(p => p.Piece));
        return result;
    }

    // MARK: Normalisierung

    internal string Normalize(string text)
    {
        var normalized = _charsMap?.Normalize(text) ?? text;
        foreach (var (pattern, content) in _replacements) normalized = pattern.Replace(normalized, content);
        return normalized;
    }

    /// <summary>Metaspace mit <c>add_prefix_space</c>: „▁“ statt Leerzeichen, Teilung vor jedem „▁“.</summary>
    private static List<string> PreTokenize(string text)
    {
        var s = text.Replace(' ', Replacement);
        if (!s.StartsWith(Replacement)) s = Replacement + s;
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] != Replacement) continue;
            words.Add(s[start..i]);
            start = i;
        }
        words.Add(s[start..]);
        return words;
    }

    // MARK: Unigram

    private IEnumerable<int> EncodeWord(string word)
    {
        var lastWasUnk = false;
        foreach (var (_, id) in Viterbi(word))
        {
            // Aufeinanderfolgende unbekannte Zeichen werden zu einem <unk> verschmolzen (fuse_unk).
            if (id == UnkId)
            {
                if (lastWasUnk) continue;
                lastWasUnk = true;
            }
            else
            {
                lastWasUnk = false;
            }
            yield return id;
        }
    }

    private struct Node
    {
        public int StartsAt;   // -1 = unerreicht
        public double Score;
        public int Id;
    }

    /// <summary>Beste Zerlegung eines Worts (Positionen in UTF-16, Schritte je Unicode-Zeichen).</summary>
    private List<(string Piece, int Id)> Viterbi(string word)
    {
        var size = word.Length;
        var best = new Node[size + 1];
        for (var i = 0; i <= size; i++) best[i] = new Node { StartsAt = -1 };
        best[0] = new Node { StartsAt = 0, Score = 0, Id = 0 };

        var startsAt = 0;
        while (startsAt < size)
        {
            var charLength = char.IsHighSurrogate(word[startsAt]) && startsAt + 1 < size && char.IsLowSurrogate(word[startsAt + 1]) ? 2 : 1;
            if (best[startsAt].StartsAt < 0 && startsAt != 0)
            {
                startsAt += charLength;
                continue;
            }
            var scoreTillHere = best[startsAt].Score;
            var hasSingleNode = false;
            var maxLength = Math.Min(_maxPieceLength, size - startsAt);
            for (var length = 1; length <= maxLength; length++)
            {
                var end = startsAt + length;
                // Nie mitten in einem Surrogat-Paar enden.
                if (end < size && char.IsLowSurrogate(word[end]) && char.IsHighSurrogate(word[end - 1])) continue;
                if (!_pieces.TryGetValue(word.Substring(startsAt, length), out var piece)) continue;
                var candidate = piece.Score + scoreTillHere;
                ref var target = ref best[end];
                if (target.StartsAt < 0 || candidate > target.Score)
                {
                    target.Score = candidate;
                    target.StartsAt = startsAt;
                    target.Id = piece.Id;
                }
                if (length == charLength) hasSingleNode = true;
            }
            if (!hasSingleNode)
            {
                ref var target = ref best[startsAt + charLength];
                var candidate = _unkScore + scoreTillHere;
                if (target.StartsAt < 0 || candidate > target.Score)
                {
                    target.Score = candidate;
                    target.StartsAt = startsAt;
                    target.Id = UnkId;
                }
            }
            startsAt += charLength;
        }

        var result = new List<(string, int)>();
        var endsAt = size;
        while (endsAt > 0)
        {
            var node = best[endsAt];
            result.Add((word[node.StartsAt..endsAt], node.Id));
            endsAt = node.StartsAt;
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// SentencePiece-Normalisierungstabelle (<c>precompiled_charsmap</c>): Double-Array-Trie (darts-clone) über UTF-8
    /// plus Ersatztexte. Nachgebaut wie <c>spm_precompiled</c> in <c>tokenizers</c> – inklusive dessen Eigenheiten
    /// (ganzes Graphem zuerst, nur wenn kürzer als 6 Bytes; erster Treffer der Präfixsuche).
    /// </summary>
    private sealed class PrecompiledCharsMap
    {
        private readonly uint[] _trie;
        private readonly byte[] _normalized;

        public PrecompiledCharsMap(byte[] data)
        {
            var trieSize = BitConverter.ToUInt32(data, 0);
            var count = (int)(trieSize / 4);
            _trie = new uint[count];
            for (var i = 0; i < count; i++) _trie[i] = BitConverter.ToUInt32(data, 4 + 4 * i);
            _normalized = data[(4 + (int)trieSize)..];
        }

        public string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length);
            var graphemes = StringInfo.GetTextElementEnumerator(text);
            while (graphemes.MoveNext())
            {
                var grapheme = (string)graphemes.Current;
                var bytes = Encoding.UTF8.GetBytes(grapheme);
                if (bytes.Length < 6 && Transform(bytes) is { } whole)
                {
                    sb.Append(whole);
                    continue;
                }
                foreach (var rune in grapheme.EnumerateRunes())
                {
                    var part = rune.ToString();
                    sb.Append(Transform(Encoding.UTF8.GetBytes(part)) ?? part);
                }
            }
            return sb.ToString();
        }

        private string? Transform(byte[] chunk)
        {
            if (FirstPrefixMatch(chunk) is not { } index) return null;
            var end = index;
            while (end < _normalized.Length && _normalized[end] != 0) end++;
            return Encoding.UTF8.GetString(_normalized, index, end - index);
        }

        private int? FirstPrefixMatch(byte[] key)
        {
            if (_trie.Length == 0) return null;
            long nodePos = 0;
            var unit = _trie[0];
            nodePos ^= Offset(unit);
            foreach (var c in key)
            {
                if (c == 0) break;
                nodePos ^= c;
                if (nodePos >= _trie.Length) return null;
                unit = _trie[nodePos];
                if (Label(unit) != c) return null;
                nodePos ^= Offset(unit);
                if (HasLeaf(unit))
                {
                    if (nodePos >= _trie.Length) return null;
                    return (int)Value(_trie[nodePos]);
                }
            }
            return null;
        }

        private static bool HasLeaf(uint unit) => ((unit >> 8) & 1) == 1;
        private static uint Value(uint unit) => unit & ((1u << 31) - 1);
        private static uint Label(uint unit) => unit & ((1u << 31) | 0xFF);
        private static long Offset(uint unit) => (long)((unit >> 10) << (int)((unit & (1u << 9)) >> 6));
    }
}
