using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Components;

/// <summary>
/// Zeigt das Markdown der Zusammenfassungen: Überschriften, Aufzählungen, nummerierte Listen, Aufgaben-Checkboxen
/// und Absätze mit Inline-Formatierung (fett, kursiv, Code, Links als Text). Bewusst klein – mehr erzeugt der
/// Zusammenfassungs-Prompt nicht (Pendant zu MarkdownView.swift).
/// </summary>
public sealed class MarkdownView : UserControl
{
    private enum Kind { Heading, Bullet, Numbered, Task, Paragraph }

    private sealed record Block(Kind Kind, string Text, int Level = 0, bool Done = false, string Marker = "");

    private const double BodySize = 13.5;

    public MarkdownView(string markdown)
    {
        var stack = new VStack { Spacing = 8 };
        var first = true;
        foreach (var block in Parse(markdown))
        {
            stack.Children.Add(View(block, first));
            first = false;
        }
        Content = stack;
    }

    // MARK: Blöcke

    private static readonly Regex NumberedPattern = new(@"^(\d{1,3})[.)]\s+(.*)$");

    private static IEnumerable<Block> Parse(string markdown)
    {
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                yield return new Block(Kind.Heading, line[level..].Trim(), level);
                continue;
            }
            var task = new[] { ("- [ ] ", false), ("- [x] ", true), ("- [X] ", true), ("* [ ] ", false), ("* [x] ", true) }
                .FirstOrDefault(p => line.StartsWith(p.Item1));
            if (task.Item1 != null)
            {
                yield return new Block(Kind.Task, line[task.Item1.Length..], Done: task.Item2);
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                yield return new Block(Kind.Bullet, line[2..].Trim());
                continue;
            }
            if (NumberedPattern.Match(line) is { Success: true } m)
            {
                yield return new Block(Kind.Numbered, m.Groups[2].Value, Marker: m.Groups[1].Value + ".");
                continue;
            }
            yield return new Block(Kind.Paragraph, line);
        }
    }

    private static FrameworkElement View(Block block, bool first)
    {
        switch (block.Kind)
        {
            case Kind.Heading:
            {
                var text = Text(block.Text, block.Level <= 2 ? 18 : 15);
                text.SetResourceReference(TextBlock.FontFamilyProperty, "Luna.Font.Title");
                text.Margin = new Thickness(0, first ? 0 : 6, 0, 0);
                return text;
            }
            case Kind.Bullet:
            {
                var dot = new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 8, 0, 0) };
                dot.SetResourceReference(Shape.FillProperty, "Luna.Lavender");
                return Row(dot, Text(block.Text));
            }
            case Kind.Numbered:
            {
                var number = new TextBlock { Text = block.Marker, FontSize = BodySize, FontWeight = FontWeights.SemiBold, MinWidth = 16 }
                    .Styled("Luna.TextBase").Brushed(TextBlock.ForegroundProperty, "Luna.Lavender");
                return Row(number, Text(block.Text));
            }
            case Kind.Task:
            {
                var box = new Border
                {
                    Width = 13, Height = 13, CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1.4),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0),
                };
                box.SetResourceReference(Border.BorderBrushProperty, "Luna.Honey");
                if (block.Done)
                {
                    box.SetResourceReference(Border.BackgroundProperty, "Luna.Honey");
                    box.Child = new LunaIcon { Kind = IconKind.Check, Size = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                        .Brushed(LunaIcon.ForegroundProperty, "Luna.OnAccent");
                }
                return Row(box, Text(block.Text));
            }
            default:
                return Text(block.Text);
        }
    }

    private static FrameworkElement Row(FrameworkElement marker, FrameworkElement text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marker.Margin = new Thickness(marker.Margin.Left, marker.Margin.Top, 9, 0);
        grid.Children.Add(marker);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock Text(string markdown, double size = BodySize)
    {
        var text = new TextBlock { FontSize = size, LineHeight = size * 1.42 }.Styled("Luna.Text");
        foreach (var inline in Inlines(markdown)) text.Inlines.Add(inline);
        return text;
    }

    // MARK: Inline

    /// <summary>**fett**, __fett__, *kursiv*, _kursiv_, `Code`, [Text](Link) → Text.</summary>
    private static readonly Regex InlinePattern = new(
        @"(\*\*(?<b>.+?)\*\*)|(__(?<b2>.+?)__)|(`(?<c>[^`]+)`)|(\[(?<l>[^\]]+)\]\([^)]*\))|(\*(?<i>[^*\s][^*]*?)\*)|((?<![\p{L}\p{N}])_(?<i2>[^_\s][^_]*?)_(?![\p{L}\p{N}]))",
        RegexOptions.Compiled);

    private static IEnumerable<Inline> Inlines(string text)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) yield return new Run(text[pos..m.Index]);
            if (m.Groups["b"].Success || m.Groups["b2"].Success)
            {
                var inner = m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value;
                var bold = new Bold();
                foreach (var i in Inlines(inner)) bold.Inlines.Add(i);
                yield return bold;
            }
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
            {
                var inner = m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value;
                var italic = new Italic();
                foreach (var i in Inlines(inner)) italic.Inlines.Add(i);
                yield return italic;
            }
            else if (m.Groups["c"].Success)
            {
                var code = new Run(m.Groups["c"].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, "Luna.SurfaceMuted");
                yield return code;
            }
            else if (m.Groups["l"].Success)
            {
                var link = new Run(m.Groups["l"].Value);
                link.SetResourceReference(TextElement.ForegroundProperty, "Luna.Lavender");
                yield return link;
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) yield return new Run(text[pos..]);
    }
}
