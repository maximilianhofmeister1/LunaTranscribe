using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using LunaCore;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Components;

/// <summary>
/// Zeigt das Markdown der Zusammenfassungen (Pendant zu MarkdownView.swift). Das Zerlegen übernimmt
/// <see cref="MarkdownBlock.Parse"/>, hier geht es nur um die Hierarchie im Bild: Unterpunkte rücken unter ihren
/// Oberpunkt und stehen enger beieinander als die Oberpunkte, sodass jedes Thema als Gruppe mit klarer Überschrift
/// lesbar ist. Inline-Formatierung: fett, kursiv, Code, Links als Text.
/// </summary>
public sealed class MarkdownView : UserControl
{
    private const double BodySize = 13.5;
    private const double MarkerSize = 5;
    private const double MarkerSpacing = 8;
    /// <summary>Etwas mehr als Punkt + Abstand: Der Unterpunkt beginnt sichtbar rechts vom Text des Oberpunkts.</summary>
    private const double IndentPerLevel = 16;
    private const double SpaceBeforeHeading = 18;
    private const double SpaceBeforeBlock = 8;
    private const double SpaceBeforeSubitem = 4;
    private const double SubitemMarkerOpacity = 0.55;

    public MarkdownView(string markdown)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var block in MarkdownBlock.Parse(markdown))
        {
            var view = View(block);
            view.Margin = new Thickness(view.Margin.Left, first ? 0 : SpaceBefore(block), 0, 0);
            stack.Children.Add(view);
            first = false;
        }
        Content = stack;
    }

    private static double SpaceBefore(MarkdownBlock block) => block switch
    {
        MarkdownBlock.Heading => SpaceBeforeHeading,
        MarkdownBlock.ListItem { Depth: > 0 } => SpaceBeforeSubitem,
        _ => SpaceBeforeBlock,
    };

    private static FrameworkElement View(MarkdownBlock block)
    {
        switch (block)
        {
            case MarkdownBlock.Heading heading:
            {
                var text = Text(heading.Text, heading.Level switch { 1 => 21, 2 => 18, _ => 15 });
                text.SetResourceReference(TextBlock.FontFamilyProperty, "Luna.Font.Title");
                return text;
            }
            case MarkdownBlock.ListItem item:
            {
                var row = Row(Marker(item.Marker, item.Depth), Text(item.Text));
                row.Margin = new Thickness(item.Depth * IndentPerLevel, 0, 0, 0);
                return row;
            }
            case MarkdownBlock.Paragraph paragraph:
                return Text(paragraph.Text);
            default:
                return new TextBlock();
        }
    }

    private static FrameworkElement Marker(MarkdownListMarker marker, int depth)
    {
        if (marker == MarkdownListMarker.Bullet)
        {
            var dot = new Ellipse
            {
                Width = MarkerSize, Height = MarkerSize, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 8, 0, 0),
                Opacity = depth == 0 ? 1 : SubitemMarkerOpacity,
            };
            dot.SetResourceReference(Shape.FillProperty, "Luna.Lavender");
            return dot;
        }
        var box = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1.4),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0),
        };
        box.SetResourceReference(Border.BorderBrushProperty, "Luna.Honey");
        if (marker == MarkdownListMarker.TaskDone)
        {
            box.SetResourceReference(Border.BackgroundProperty, "Luna.Honey");
            box.Child = new LunaIcon { Kind = IconKind.Check, Size = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                .Brushed(LunaIcon.ForegroundProperty, "Luna.OnAccent");
        }
        return box;
    }

    private static FrameworkElement Row(FrameworkElement marker, FrameworkElement text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marker.Margin = new Thickness(marker.Margin.Left, marker.Margin.Top, MarkerSpacing, 0);
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
