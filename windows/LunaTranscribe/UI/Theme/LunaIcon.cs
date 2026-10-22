using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LunaTranscribe.UI.Theme;

/// <summary>Symbole der Oberfläche – Pendant zu den SF Symbols der macOS-Version.</summary>
public enum IconKind
{
    None,
    // Eigene Formen (Mondnacht)
    Moon, MoonStars, Sparkles, Waveform, Sparkle,
    // Segoe Fluent Icons
    People, Book, History, Sliders, Mic, Stop, Close, Gear, Window, Copy, Search, Folder, Refresh, Trash, Share,
    Calendar, Clock, TextLines, Cpu, Lock, Unlock, Pencil, Keyboard, CheckCircle, Warning, ErrorCircle, Cloud,
    Record, Hourglass, ChevronDown, ChevronUp, ChevronRight, Plus, Minus, Check, Drive, Power, Speaker, Shield,
    Globe, Link, Play, Info, Download, More,
}

/// <summary>
/// Zeichnet ein Symbol in der Vordergrundfarbe: Segoe-Fluent-Glyphen (Fallback MDL2 unter Windows 10)
/// oder eigene Vektorformen für Mond, Sterne und Wellenform.
/// </summary>
public sealed class LunaIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(LunaIcon),
        new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LunaIcon),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LunaIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public IconKind Kind { get => (IconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static Typeface? _typeface;

    private static readonly Dictionary<IconKind, char> Glyphs = new()
    {
        [IconKind.People] = '', [IconKind.Book] = '', [IconKind.History] = '',
        [IconKind.Sliders] = '', [IconKind.Mic] = '', [IconKind.Stop] = '',
        [IconKind.Close] = '', [IconKind.Gear] = '', [IconKind.Window] = '',
        [IconKind.Copy] = '', [IconKind.Search] = '', [IconKind.Folder] = '',
        [IconKind.Refresh] = '', [IconKind.Trash] = '', [IconKind.Share] = '',
        [IconKind.Calendar] = '', [IconKind.Clock] = '', [IconKind.TextLines] = '',
        [IconKind.Cpu] = '', [IconKind.Lock] = '', [IconKind.Unlock] = '',
        [IconKind.Pencil] = '', [IconKind.Keyboard] = '', [IconKind.CheckCircle] = '',
        [IconKind.Warning] = '', [IconKind.ErrorCircle] = '', [IconKind.Cloud] = '',
        [IconKind.Record] = '', [IconKind.Hourglass] = '', [IconKind.ChevronDown] = '',
        [IconKind.ChevronUp] = '', [IconKind.ChevronRight] = '', [IconKind.Plus] = '',
        [IconKind.Minus] = '', [IconKind.Check] = '', [IconKind.Drive] = '',
        [IconKind.Power] = '', [IconKind.Speaker] = '', [IconKind.Shield] = '',
        [IconKind.Globe] = '', [IconKind.Link] = '', [IconKind.Play] = '',
        [IconKind.Info] = '', [IconKind.Download] = '', [IconKind.More] = '',
    };

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var kind = Kind;
        if (kind == IconKind.None) return;
        var s = Size;
        if (Glyphs.TryGetValue(kind, out var glyph))
        {
            _typeface ??= new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typeface, s, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((s - text.Width) / 2, (s - text.Height) / 2));
            return;
        }
        var geometry = Shape(kind);
        if (geometry == null) return;
        dc.PushTransform(new ScaleTransform(s / 24.0, s / 24.0));
        dc.DrawGeometry(Foreground, null, geometry);
        dc.Pop();
    }

    private static readonly Dictionary<IconKind, Geometry> Shapes = new();

    /// <summary>Eigene Formen im 24er-Raster.</summary>
    public static Geometry? Shape(IconKind kind)
    {
        if (Shapes.TryGetValue(kind, out var cached)) return cached;
        Geometry? g = kind switch
        {
            IconKind.Moon => Crescent(11.5, 12.5, 9, 4.6, -3.6, 7.6),
            IconKind.MoonStars => Group(Crescent(10, 14, 8.5, 4.4, -3.4, 7.2), SparklePath(19, 5, 3.4), SparklePath(20.5, 12.5, 1.9)),
            IconKind.Sparkles => Group(SparklePath(10, 14, 8), SparklePath(18.5, 5, 3.6), SparklePath(19.5, 18.5, 2.2)),
            IconKind.Sparkle => SparklePath(12, 12, 10),
            IconKind.Waveform => Bars(),
            _ => null,
        };
        if (g != null) { g.Freeze(); Shapes[kind] = g; }
        return g;
    }

    /// <summary>Mondsichel: Kreis, aus dem ein versetzter Kreis herausgeschnitten wird (wie im App-Icon).</summary>
    public static Geometry Crescent(double cx, double cy, double r, double dx, double dy, double hollowR) =>
        new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), r, r),
            new EllipseGeometry(new Point(cx + dx, cy + dy), hollowR, hollowR));

    /// <summary>Vierzackiger, weicher Stern (wie drawSparkle in make-icon.swift).</summary>
    public static Geometry SparklePath(double x, double y, double r)
    {
        var inner = r * 0.28;
        var fig = new PathFigure { StartPoint = new Point(x, y - r), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + inner, y - inner), new Point(x + r, y), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x + inner, y + inner), new Point(x, y + r), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x - inner, y + inner), new Point(x - r, y), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(x - inner, y - inner), new Point(x, y - r), true));
        return new PathGeometry(new[] { fig });
    }

    private static Geometry Bars()
    {
        double[] heights = { 6, 12, 18, 10, 14, 7 };
        var g = new GeometryGroup();
        var x = 2.5;
        foreach (var h in heights)
        {
            g.Children.Add(new RectangleGeometry(new Rect(x, 12 - h / 2, 2.4, h), 1.2, 1.2));
            x += 3.4;
        }
        return g;
    }

    private static Geometry Group(params Geometry[] parts)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var p in parts) g.Children.Add(p);
        return g;
    }
}
