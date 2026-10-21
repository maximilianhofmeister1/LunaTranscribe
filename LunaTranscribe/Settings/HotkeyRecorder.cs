using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaTranscribe.Hotkeys;

namespace LunaTranscribe.Settings;

/// <summary>
/// Eingabefeld zum Aufzeichnen einer Tastenkombination (Pendant zu KeyboardShortcuts.Recorder).
/// Fokus → Taste(n) drücken → gespeichert. Rücktaste/Entf löscht, Esc bricht ab.
/// Solange das Feld den Fokus hat, ist der globale Hook pausiert (sonst würde er F2 verschlucken).
/// </summary>
public sealed class HotkeyRecorder : Control
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(Hotkey), typeof(HotkeyRecorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyRecorder)d).Refresh()));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(HotkeyRecorder), new PropertyMetadata(true));

    /// <summary>Wird gesetzt, solange das Feld den Fokus hat.</summary>
    public event Action<bool>? CapturingChanged;

    public Hotkey? Hotkey
    {
        get => (Hotkey?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    private readonly Border _border;
    private readonly TextBlock _label;
    private readonly Button _clear;

    public HotkeyRecorder()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        MinWidth = 160;
        _label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        _clear = new Button
        {
            Content = "✕", FontSize = 10, Padding = new Thickness(6, 1, 6, 1), MinHeight = 0,
            Margin = new Thickness(0, 2, 2, 2), Focusable = false, ToolTip = "Hotkey entfernen",
        };
        _clear.Click += (_, _) => { Hotkey = Hotkeys.Hotkey.None; };
        var dock = new DockPanel();
        DockPanel.SetDock(_clear, Dock.Right);
        dock.Children.Add(_clear);
        dock.Children.Add(_label);
        _border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.White,
            MinHeight = 28,
            Child = dock,
        };
        AddVisualChild(_border);
        AddLogicalChild(_border);

        MouseLeftButtonDown += (_, e) => { Focus(); e.Handled = true; };
        GotKeyboardFocus += (_, _) => { CapturingChanged?.Invoke(true); Refresh(); };
        LostKeyboardFocus += (_, _) => { CapturingChanged?.Invoke(false); Refresh(); };
        Refresh();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _border;

    protected override Size MeasureOverride(Size constraint)
    {
        _border.Measure(constraint);
        return _border.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _border.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsKeyboardFocused) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return;   // nur Modifier – auf die eigentliche Taste warten
            case Key.Escape:
                Keyboard.ClearFocus();
                return;
            case Key.Back or Key.Delete when AllowEmpty:
                Hotkey = Hotkeys.Hotkey.None;
                Keyboard.ClearFocus();
                return;
            case Key.Tab:
                return;
        }

        var mods = HotkeyModifiers.None;
        var km = Keyboard.Modifiers;
        if (km.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (km.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (km.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (km.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        Hotkey = new Hotkey(vk, mods);
        Keyboard.ClearFocus();
    }

    private void Refresh()
    {
        var hk = Hotkey;
        var capturing = IsKeyboardFocused;
        _label.Text = capturing ? "Taste drücken…" : (hk is { IsSet: true } ? hk.DisplayName : "Kein Hotkey");
        _label.Foreground = capturing || hk is not { IsSet: true }
            ? new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
            : Brushes.Black;
        _border.BorderBrush = capturing
            ? new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x78))
            : new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD6));
        _clear.Visibility = AllowEmpty && hk is { IsSet: true } && !capturing ? Visibility.Visible : Visibility.Collapsed;
    }
}
