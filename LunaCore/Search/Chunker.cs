using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LunaCore;

public enum ChunkKind
{
    Turns = 0,
    Summary = 1,
}

/// <summary>Bereich von Beiträgen (Indizes in <see cref="MeetingTranscript.Turns"/>), beide Grenzen einschließlich.</summary>
public readonly record struct TurnRange(int Lower, int Upper)
{
    public IEnumerable<int> Indices => Enumerable.Range(Lower, Upper - Lower + 1);
    public override string ToString() => $"{Lower}...{Upper}";
}

/// <summary>
/// Ein Suchabschnitt: aufeinanderfolgende Beiträge (oder ein Abschnitt der Zusammenfassung), klein genug
/// für das Embedding-Modell und groß genug, um als Treffer für sich zu stehen.
/// </summary>
/// <param name="Turns">Beitragsbereich; null bei der Zusammenfassung.</param>
/// <param name="SpeakerIDs">Sprecher-IDs, keine Namen: Umbenennen macht den Index so nicht ungültig.</param>
public sealed record Chunk(ChunkKind Kind, TurnRange? Turns, double Start, double End, IReadOnlyList<string> SpeakerIDs, string Text);

/// <summary>
/// Zerlegt ein Meeting in Suchabschnitte.
/// <list type="bullet">
/// <item>Beiträge werden bis etwa <see cref="TargetWords"/> Wörter zusammengefasst; der letzte Beitrag eines Abschnitts
/// eröffnet den nächsten noch einmal (Überlappung), sofern er kurz ist – so reißt kein Gedanke an der Grenze ab.</item>
/// <item>Sehr lange Beiträge werden an Satzgrenzen geteilt.</item>
/// <item>Eingebettet wird nur der gesprochene Text, ohne Sprechernamen.</item>
/// <item>~200 Wörter bleiben sicher unter den 512 Token von <c>multilingual-e5-small</c>.</item>
/// </list>
/// </summary>
public static class Chunker
{
    public const int TargetWords = 200;
    /// <summary>Überlappung nur mit Beiträgen bis zu dieser Länge, sonst würden Abschnitte fast doppelt indexiert.</summary>
    internal const int MaxOverlapWords = 60;
    /// <summary>Geht in das Änderungsmerkmal des Index ein: Ändert sich die Zerlegung, wird neu indexiert.</summary>
    public const int Version = 1;

    private sealed record Piece(int Turn, double Start, double End, string SpeakerID, string Text, int Words);

    public static List<Chunk> Chunks(MeetingTranscript transcript) =>
        TurnChunks(transcript).Concat(SummaryChunks(transcript)).ToList();

    private static List<Chunk> TurnChunks(MeetingTranscript transcript)
    {
        var pieces = transcript.Turns
            .SelectMany((turn, index) => Split(turn.Text)
                .Select(text => new Piece(index, turn.Start, turn.End, turn.SpeakerID, text, WordCount(text))))
            .ToList();

        var chunks = new List<Chunk>();
        var window = new List<Piece>();
        var words = 0;
        // Enthält das Fenster mehr als die übernommene Überlappung? Nur dann lohnt ein eigener Abschnitt.
        var hasNewContent = false;
        foreach (var piece in pieces)
        {
            if (hasNewContent && words + piece.Words > TargetWords)
            {
                chunks.Add(MakeChunk(window));
                var last = window[^1];
                var carry = last.Words <= MaxOverlapWords ? last : null;
                window = carry is null ? new List<Piece>() : new List<Piece> { carry };
                words = carry?.Words ?? 0;
                hasNewContent = false;
            }
            window.Add(piece);
            words += piece.Words;
            hasNewContent = true;
        }
        if (hasNewContent) chunks.Add(MakeChunk(window));
        return chunks;
    }

    private static Chunk MakeChunk(List<Piece> window)
    {
        var speakers = new List<string>();
        foreach (var piece in window)
            if (!speakers.Contains(piece.SpeakerID)) speakers.Add(piece.SpeakerID);
        return new Chunk(ChunkKind.Turns,
                         new TurnRange(window[0].Turn, window[^1].Turn),
                         window[0].Start,
                         window[^1].End,
                         speakers,
                         string.Join("\n", window.Select(p => p.Text)));
    }

    /// <summary>Zusammenfassung nach Überschriften („## Aufgaben“ …) geteilt; jeder Abschnitt ein Treffer.</summary>
    private static IEnumerable<Chunk> SummaryChunks(MeetingTranscript transcript)
    {
        var markdown = transcript.Summary?.Markdown;
        if (string.IsNullOrEmpty(markdown)) return Enumerable.Empty<Chunk>();
        var sections = new List<List<string>> { new() };
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith('#') && sections[^1].Count > 0) sections.Add(new List<string>());
            sections[^1].Add(line);
        }
        return sections
            .Select(s => string.Join("\n", s).Trim())
            .Where(s => s.Length > 0)
            .SelectMany(Split)
            .Select(text => new Chunk(ChunkKind.Summary, null, 0, 0, Array.Empty<string>(), text))
            .ToList();
    }

    /// <summary>Teilt Texte über <see cref="TargetWords"/> an Satzenden; kürzere bleiben unverändert.</summary>
    internal static List<string> Split(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return new List<string>();
        if (WordCount(trimmed) <= TargetWords) return new List<string> { trimmed };

        var sentences = new List<string>();
        var current = new StringBuilder();
        foreach (var character in trimmed)
        {
            current.Append(character);
            if (character is '.' or '!' or '?' or '\n')
            {
                sentences.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) sentences.Add(current.ToString());

        var parts = new List<string>();
        var part = "";
        foreach (var sentence in sentences)
        {
            if (part.Length > 0 && WordCount(part) + WordCount(sentence) > TargetWords)
            {
                parts.Add(part.Trim());
                part = "";
            }
            part += sentence;
        }
        if (part.Trim().Length > 0) parts.Add(part.Trim());
        // Ein einzelner „Satz“ ohne Satzzeichen kann länger sein – dann hart nach Wörtern teilen.
        return parts.SelectMany(p => WordCount(p) > TargetWords * 2 ? HardSplit(p) : new List<string> { p }).ToList();
    }

    private static List<string> HardSplit(string text)
    {
        var words = TextUtil.SplitWhitespace(text);
        var result = new List<string>();
        for (var i = 0; i < words.Count; i += TargetWords)
            result.Add(string.Join(" ", words.Skip(i).Take(TargetWords)));
        return result;
    }

    public static int WordCount(string text) => TextUtil.SplitWhitespace(text).Count;
}
