using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LunaTranscribe.UI.Theme;

// Wiederverwendbare Bausteine im Mondnacht-Stil (Pendant zu UI/Theme/Components.swift). Aussehen in Styles.xaml.

/// <summary>Angehängte Eigenschaften für Buttons &amp; Co.: Symbol vor der Beschriftung.</summary>
public static class Luna
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(IconKind), typeof(Luna), new FrameworkPropertyMetadata(IconKind.None));

    public static IconKind GetIcon(DependencyObject d) => (IconKind)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, IconKind value) => d.SetValue(IconProperty, value);

    /// <summary>Platzhaltertext für Eingabefelder.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Luna), new FrameworkPropertyMetadata(""));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>Zahl in einer Plakette (Seitenleiste: Wörterbuch-Vorschläge). 0 = keine.</summary>
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(int), typeof(Luna), new FrameworkPropertyMetadata(0));

    public static int GetBadge(DependencyObject d) => (int)d.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject d, int value) => d.SetValue(BadgeProperty, value);
}

/// <summary>Karte: Oberfläche, Haarlinie, weicher Schatten, Eckradius 16.</summary>
public class LunaCard : ContentControl
{
    static LunaCard() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LunaCard), new FrameworkPropertyMetadata(typeof(LunaCard)));
}

/// <summary>Seite im Hauptfenster: verspielter Titel, Untertitel, optionales Zubehör rechts, scrollender Inhalt.</summary>
public class LunaPage : ContentControl
{
    static LunaPage() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LunaPage), new FrameworkPropertyMetadata(typeof(LunaPage)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(LunaPage));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(LunaPage));
    public static readonly DependencyProperty AccessoryProperty = DependencyProperty.Register(nameof(Accessory), typeof(object), typeof(LunaPage));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Accessory { get => GetValue(AccessoryProperty); set => SetValue(AccessoryProperty, value); }
}

/// <summary>Überschrift einer Karte oder eines Abschnitts (Symbol in Lavendel + Fraunces-Titel).</summary>
public class SectionTitle : Control
{
    static SectionTitle() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SectionTitle), new FrameworkPropertyMetadata(typeof(SectionTitle)));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionTitle));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(IconKind), typeof(SectionTitle));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public IconKind Icon { get => (IconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// <summary>Kleines farbiges Etikett („Zusammenfassung“, „3 Sprecher“). Hintergrund = Tönung mit 13 % Deckkraft.</summary>
public class Chip : Control
{
    static Chip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Chip), new FrameworkPropertyMetadata(typeof(Chip)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Chip));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(IconKind), typeof(Chip));

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IconKind Icon { get => (IconKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// <summary>Tastenkappe für Kürzel-Anzeigen.</summary>
public class Keycap : Control
{
    static Keycap() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Keycap), new FrameworkPropertyMetadata(typeof(Keycap)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(Keycap));
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Erklärtext, der erst auf Wunsch aufklappt – hält Einstellungsseiten ruhig.</summary>
public class LearnMore : Control
{
    static LearnMore() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LearnMore), new FrameworkPropertyMetadata(typeof(LearnMore)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LearnMore));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(LearnMore));

    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
}

/// <summary>Einheitlicher Ladezustand (ASR-Modell, Sprachmodell, Wörterbuch) – wie LoadStatus in Swift.</summary>
public sealed record LoadStatus(LoadPhase Phase, string Text, double? Fraction = null);

public enum LoadPhase { Idle, Loading, Ready, Warning, Failed }

/// <summary>Zeigt einen <see cref="LoadStatus"/>: Fortschritt/Symbol + Text, bei Fehler „Erneut versuchen“.</summary>
public sealed class LoadStatusView : ContentControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(LoadStatus), typeof(LoadStatusView), new PropertyMetadata(null, (d, _) => ((LoadStatusView)d).Rebuild()));

    public LoadStatus? Status { get => (LoadStatus?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    /// <summary>Aktion für „Erneut versuchen“ (nur bei Fehler sichtbar).</summary>
    public Action? Retry { get; set; }

    private void Rebuild()
    {
        var status = Status;
        if (status == null) { Content = null; return; }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement indicator;
        switch (status.Phase)
        {
            case LoadPhase.Loading when status.Fraction is { } f:
                var bar = new ProgressBar { Width = 90, Height = 4, Minimum = 0, Maximum = 1, Value = f, VerticalAlignment = VerticalAlignment.Center };
                indicator = bar;
                break;
            case LoadPhase.Loading:
                indicator = new Spinner { Width = 14, Height = 14 };
                break;
            default:
                var icon = new LunaIcon { Size = 14, VerticalAlignment = VerticalAlignment.Center };
                (icon.Kind, var brush) = status.Phase switch
                {
                    LoadPhase.Ready => (IconKind.CheckCircle, "Luna.Sage"),
                    LoadPhase.Warning => (IconKind.Warning, "Luna.Honey"),
                    LoadPhase.Failed => (IconKind.ErrorCircle, "Luna.Rose"),
                    _ => (IconKind.Moon, "Luna.InkMuted"),
                };
                icon.SetResourceReference(LunaIcon.ForegroundProperty, brush);
                indicator = icon;
                break;
        }
        indicator.Margin = new Thickness(0, 0, 8, 0);
        indicator.VerticalAlignment = VerticalAlignment.Top;
        if (indicator is not ProgressBar) indicator.Margin = new Thickness(0, 1, 8, 0);
        else indicator.Margin = new Thickness(0, 6, 8, 0);
        row.Children.Add(indicator);

        var text = new TextBlock { Text = status.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        text.SetResourceReference(StyleProperty, "Luna.Caption");
        text.SetResourceReference(TextBlock.ForegroundProperty, status.Phase == LoadPhase.Failed ? "Luna.Rose" : "Luna.InkMuted");
        row.Children.Add(text);

        if (status.Phase == LoadPhase.Failed && Retry != null)
        {
            var button = new Button { Content = "Erneut versuchen", Margin = new Thickness(6, -4, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            button.SetResourceReference(StyleProperty, "Luna.Quiet");
            button.Click += (_, _) => Retry?.Invoke();
            row.Children.Add(button);
        }
        Content = row;
    }
}

/// <summary>Kleiner, drehender Ladekreis (Pendant zu ProgressView() in klein).</summary>
public sealed class Spinner : FrameworkElement
{
    private readonly RotateTransform _rotate = new();
    private readonly System.Windows.Media.Animation.DoubleAnimation _spin =
        new(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(Spinner), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Foreground { get => (Brush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public Spinner()
    {
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, IsVisible ? _spin : null);
        if (ReadLocalValue(ForegroundProperty) == DependencyProperty.UnsetValue) SetResourceReference(ForegroundProperty, "Luna.InkMuted");
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var r = size / 2 - 1.2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var brush = Foreground ?? Brushes.Gray;
        var track = brush.Clone();
        track.Opacity = 0.25;
        dc.DrawEllipse(null, new Pen(track, 2), c, r, r);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}

/// <summary>Lebendige Wellenform aus dem Eingangspegel: Balken schwingen phasenversetzt, ihre Höhe folgt dem Pegel.</summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public int BarCount { get; set; } = 9;

    private double _smoothed;

    public WaveformView()
    {
        Height = 22;
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= OnFrame;
            if (IsVisible) CompositionTarget.Rendering += OnFrame;
        };
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(BarCount * 3 + (BarCount - 1) * 3, 22);

    protected override void OnRender(DrawingContext dc)
    {
        var t = Environment.TickCount64 / 1000.0;
        _smoothed += (Math.Clamp(Level, 0, 1) - _smoothed) * 0.35;
        for (var i = 0; i < BarCount; i++)
        {
            var wobble = (Math.Sin(t * 7 + i * 0.9) + 1) / 2;
            var h = 4 + _smoothed * 18 * (0.45 + 0.55 * wobble);
            var x = i * 6;
            dc.DrawRoundedRectangle(BarBrush, null, new Rect(x, (22 - h) / 2, 3, h), 1.5, 1.5);
        }
    }
}

/// <summary>Popup-Menü für Buttons (Exportieren, Sprachauswahl): öffnet das ContextMenu linksbündig darunter.</summary>
public static class MenuButton
{
    public static void Open(Button button)
    {
        if (button.ContextMenu == null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
}

/// <summary>Kleine Helfer für Code-erzeugte Oberflächen.</summary>
public static class UiHelpers
{
    public static T Brushed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static T Styled<T>(this T element, string styleKey) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    public static void CopyToClipboard(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(30); }
        }
    }

    /// <summary>Hand-Cursor + Klick-Aktion für beliebige Elemente (statt Button, wenn der Inhalt frei gestaltet ist).</summary>
    public static T OnClick<T>(this T element, Action action) where T : UIElement
    {
        if (element is FrameworkElement fe) fe.Cursor = Cursors.Hand;
        element.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        return element;
    }
}

/// <summary>true, wenn eine Zahl größer als 0 ist (Plaketten ein-/ausblenden).</summary>
public sealed class PositiveConverter : System.Windows.Data.IValueConverter
{
    public static readonly PositiveConverter Instance = new();
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is int i && i > 0;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Eckradius = halbe Höhe → echte Kapsel. (WPF macht aus zu großen Radien wie 999 eine Ellipse,
/// weil es jede Seite getrennt normalisiert.) Verwendung: CornerRadius="{Binding ActualHeight, RelativeSource={RelativeSource Self}, Converter=…}".
/// </summary>
public sealed class CapsuleRadiusConverter : System.Windows.Data.IValueConverter
{
    public static readonly CapsuleRadiusConverter Instance = new();
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        new CornerRadius(value is double h && h > 0 ? h / 2 : 0);
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}
