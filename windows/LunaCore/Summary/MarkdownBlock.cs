using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

public enum MarkdownListMarker { Bullet, TaskOpen, TaskDone }

/// <summary>
/// Ein Block aus dem Markdown der Zusammenfassungen: Überschrift, Listenpunkt oder Absatz (Port von
/// <c>MarkdownBlock.swift</c>). Bewusst klein – mehr erzeugt der Zusammenfassungs-Prompt nicht. Inline-Formatierung
/// (fett, kursiv) bleibt im Text stehen und ist Sache der Darstellung.
/// </summary>
public abstract record MarkdownBlock
{
    public sealed record Heading(string Text, int Level) : MarkdownBlock;

    /// <param name="Depth">0 ist die oberste Ebene, jede Unterliste liegt eine Ebene tiefer.</param>
    public sealed record ListItem(string Text, MarkdownListMarker Marker, int Depth) : MarkdownBlock;

    public sealed record Paragraph(string Text) : MarkdownBlock;

    private const int MaxHeadingLevel = 6;
    private const int TabWidth = 4;
    private static readonly string[] BulletPrefixes = { "- ", "* ", "+ ", "• " };
    private static readonly (string Prefix, MarkdownListMarker Marker)[] TaskPrefixes =
        { ("[ ] ", MarkdownListMarker.TaskOpen), ("[x] ", MarkdownListMarker.TaskDone), ("[X] ", MarkdownListMarker.TaskDone) };

    /// <summary>
    /// Zerlegt Markdown zeilenweise. Leerzeilen trennen nur, sie beenden keine Liste – Modelle setzen sie gern zwischen
    /// zwei Oberpunkte. Alles andere als ein Listenpunkt beendet die Liste.
    /// </summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var nesting = new ListNesting();
        var blocks = new List<MarkdownBlock>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim(' ', '\t', '\r');
            if (line.Length == 0) continue;
            if (ListItemIn(line) is { } item)
            {
                blocks.Add(new ListItem(item.Text, item.Marker, nesting.Depth(IndentWidth(raw))));
                continue;
            }
            nesting.Reset();
            blocks.Add(HeadingIn(line) ?? (MarkdownBlock)new Paragraph(line));
        }
        return blocks;
    }

    private static Heading? HeadingIn(string line)
    {
        var level = line.TakeWhile(c => c == '#').Count();
        if (level < 1 || level > MaxHeadingLevel || line.Length <= level || line[level] != ' ') return null;
        return new Heading(line[level..].Trim(' ', '\t'), level);
    }

    private static (string Text, MarkdownListMarker Marker)? ListItemIn(string line)
    {
        var bullet = BulletPrefixes.FirstOrDefault(p => line.StartsWith(p, StringComparison.Ordinal));
        if (bullet == null) return null;
        var text = line[bullet.Length..];
        foreach (var (prefix, marker) in TaskPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return (text[prefix.Length..], marker);
        return (text, MarkdownListMarker.Bullet);
    }

    private static int IndentWidth(string line) =>
        line.TakeWhile(c => c is ' ' or '\t').Sum(c => c == '\t' ? TabWidth : 1);

    /// <summary>
    /// Leitet die Listentiefe aus der Einrückung ab, ohne eine feste Schrittweite anzunehmen: Modelle rücken mal um zwei,
    /// mal um vier Zeichen ein. Wie bei CommonMark zählt die Einrückung relativ zum Elternpunkt, und erst ab zwei Zeichen
    /// ist ein Punkt dessen Kind (eine einzelne Leerstelle ist Zufall).
    /// </summary>
    private sealed class ListNesting
    {
        private const int MinimumStep = 2;

        /// <summary>Einrückung der offenen Ebenen, von außen nach innen.</summary>
        private readonly List<int> _indents = new();

        public int Depth(int indent)
        {
            while (_indents.Count > 0 && _indents[^1] > indent) _indents.RemoveAt(_indents.Count - 1);
            if (_indents.Count > 0 && indent < _indents[^1] + MinimumStep)
                return _indents.Count - 1;   // Geschwister des innersten offenen Punkts
            _indents.Add(indent);
            return _indents.Count - 1;
        }

        public void Reset() => _indents.Clear();
    }
}
