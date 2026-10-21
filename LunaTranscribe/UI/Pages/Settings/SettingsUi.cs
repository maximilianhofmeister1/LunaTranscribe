using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LunaTranscribe.Core;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

/// <summary>
/// Grundlage eines Einstellungsbereichs: baut seinen Inhalt in <see cref="Build"/> und baut ihn neu, wenn sich
/// Einstellungen oder App-Zustand von außen ändern. Änderungen aus dem eigenen Bereich lösen keinen Neuaufbau
/// aus (sonst verlöre z. B. ein Textfeld beim Tippen den Fokus).
/// </summary>
public abstract class SettingsSectionView : UserControl
{
    protected readonly AppState State;
    private readonly VStack _root = new() { Spacing = 20 };
    private bool _selfChange;
    private bool _rebuildPending;

    protected SettingsSectionView(AppState state)
    {
        State = state;
        Content = _root;
        Loaded += (_, _) =>
        {
            State.Settings.PropertyChanged += OnExternalChange;
            State.PropertyChanged += OnExternalChange;
            State.Models.PropertyChanged += OnExternalChange;
            Subscribe();
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            State.Settings.PropertyChanged -= OnExternalChange;
            State.PropertyChanged -= OnExternalChange;
            State.Models.PropertyChanged -= OnExternalChange;
            Unsubscribe();
        };
    }

    /// <summary>Weitere Quellen abonnieren (z. B. Sprachmodell-Manager).</summary>
    protected virtual void Subscribe() { }
    protected virtual void Unsubscribe() { }

    /// <summary>Nur diese Eigenschaften lösen einen Neuaufbau aus; null = alle.</summary>
    protected virtual bool Affects(object? sender, string? property) =>
        property is not (nameof(AppState.InputLevel) or nameof(AppState.Status) or nameof(AppState.NoticeText)
            or nameof(AppState.TranscribingLabel) or nameof(AppState.StatusText));

    protected void OnExternalChange(object? sender, PropertyChangedEventArgs e)
    {
        if (_selfChange || !Affects(sender, e.PropertyName) || _rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(() => { _rebuildPending = false; Rebuild(); });
    }

    protected void Rebuild()
    {
        _root.Children.Clear();
        Build(_root);
    }

    protected abstract void Build(VStack root);

    /// <summary>Führt eine Änderung aus, ohne dass sich der Bereich deswegen neu aufbaut.</summary>
    protected void Quietly(Action change)
    {
        _selfChange = true;
        try { change(); }
        finally { _selfChange = false; }
    }

    // MARK: Bausteine (wie SettingsCard/SettingRow/ToggleRow/PromptEditor in Swift)

    /// <summary>Karte mit Titel für eine Gruppe von Einstellungen.</summary>
    protected static LunaCard Card(string title, IconKind icon, params UIElement?[] children)
    {
        var stack = new VStack { Spacing = 14 };
        stack.Children.Add(new SectionTitle { Title = title, Icon = icon });
        foreach (var c in children) if (c != null) stack.Children.Add(c);
        return new LunaCard { Padding = new Thickness(18), Content = stack };
    }

    /// <summary>Eine Einstellung: Beschriftung (optional mit Hinweis) links, Bedienelemente rechts.</summary>
    protected static FrameworkElement Row(string label, string? hint, params FrameworkElement[] controls)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = label, FontSize = 13.5 }.Styled("Luna.Text"));
        if (!string.IsNullOrEmpty(hint)) left.Children.Add(new TextBlock { Text = hint, Margin = new Thickness(0, 2, 0, 0) }.Styled("Luna.Hint"));
        grid.Children.Add(left);
        var right = new HStack { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) { c.VerticalAlignment = VerticalAlignment.Center; right.Children.Add(c); }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Schalter als Zeile.</summary>
    protected FrameworkElement ToggleRow(string label, string? hint, bool value, Action<bool> set, bool enabled = true)
    {
        var sw = new CheckBox { IsChecked = value, IsEnabled = enabled }.Styled("Luna.Switch");
        sw.Click += (_, _) => Quietly(() => set(sw.IsChecked == true));
        return Row(label, hint, sw);
    }

    /// <summary>Auswahl (Picker) mit Anzeigetexten.</summary>
    protected ComboBox Picker<T>(IEnumerable<T> items, T selected, Func<T, string> label, Action<T> set, double width = 0)
    {
        var list = items.ToList();
        var box = new ComboBox();
        if (width > 0) box.Width = width;
        foreach (var item in list) box.Items.Add(new ComboBoxItem { Content = label(item), Tag = item });
        box.SelectedIndex = list.FindIndex(i => Equals(i, selected));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T value }) Quietly(() => set(value));
        };
        return box;
    }

    /// <summary>Segmentierte Auswahl (z. B. Gedrückt halten / Ein/Aus).</summary>
    protected FrameworkElement Segmented<T>(IEnumerable<T> items, T selected, Func<T, string> label, Action<T> set)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items)
        {
            var rb = new RadioButton { Content = label(item), GroupName = group, IsChecked = Equals(item, selected) }.Styled("Luna.Segment");
            rb.Checked += (_, _) => Quietly(() => set(item));
            stack.Children.Add(rb);
        }
        return new Border
        {
            Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Child = stack,
        }.Brushed(Border.BackgroundProperty, "Luna.SurfaceMuted");
    }

    /// <summary>Mehrzeiliger Prompt-Editor mit „Auf Standard zurücksetzen“.</summary>
    protected FrameworkElement PromptEditor(string text, bool isDefault, Action<string> set, Action reset)
    {
        var stack = new VStack { Spacing = 6 };
        var editor = new TextBox { Text = text, MinHeight = 160, MaxHeight = 320 }.Styled("Luna.CodeEditor");
        var resetButton = new Button { Content = "Auf Standard zurücksetzen", HorizontalAlignment = HorizontalAlignment.Left, Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible }
            .Styled("Luna.Quiet");
        editor.TextChanged += (_, _) => Quietly(() => set(editor.Text));
        resetButton.Click += (_, _) => { reset(); Rebuild(); };
        stack.Children.Add(editor);
        stack.Children.Add(resetButton);
        return stack;
    }

    protected static TextBlock Hint(string text) => new TextBlock { Text = text }.Styled("Luna.Hint");

    protected static TextBlock Error(string text) =>
        new TextBlock { Text = text }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, "Luna.Rose");

    protected static Button Button(string text, Action click, string style = "Luna.Secondary", IconKind icon = IconKind.None)
    {
        var b = new Button { Content = text }.Styled(style);
        if (icon != IconKind.None) Luna.SetIcon(b, icon);
        b.Click += (_, _) => click();
        return b;
    }

    protected static Button IconButton(IconKind icon, string tooltip, Action click)
    {
        var b = new Button { ToolTip = tooltip }.Styled("Luna.Quiet");
        Luna.SetIcon(b, icon);
        b.Click += (_, _) => click();
        return b;
    }
}
