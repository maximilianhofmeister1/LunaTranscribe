using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Components;

/// <summary>Knopf eines <see cref="LunaDialog"/>.</summary>
/// <param name="Style">Luna-Buttonstil, z. B. „Luna.Primary“ oder „Luna.Destructive“.</param>
/// <param name="IsDefault">Reagiert auf Enter.</param>
public sealed record DialogButton(string Text, string Style = "Luna.Secondary", bool IsDefault = false);

/// <summary>
/// Rückfrage im Mondnacht-Stil (Pendant zu .alert/.confirmationDialog bzw. NSAlert): Titel, Text, Knöpfe rechts.
/// Escape oder Klick neben den Dialog = Abbrechen (-1). Modal zum Besitzerfenster.
/// </summary>
public sealed class LunaDialog : Window
{
    private int _result = -1;

    private LunaDialog(string title, string message, DialogButton[] buttons)
    {
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        SetResourceReference(FontFamilyProperty, "Luna.Font.Text");
        SetResourceReference(ForegroundProperty, "Luna.Ink");

        var stack = new VStack { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 17, TextWrapping = TextWrapping.Wrap }.Styled("Luna.Title"));
        if (!string.IsNullOrEmpty(message))
            stack.Children.Add(new TextBlock { Text = message, LineHeight = 19 }.Styled("Luna.TextMuted"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        // Knöpfe in umgekehrter Reihenfolge: der erste (wichtigste) steht rechts außen wie unter Windows üblich.
        for (var i = buttons.Length - 1; i >= 0; i--)
        {
            var index = i;
            var b = new Button { Content = buttons[i].Text, IsDefault = buttons[i].IsDefault, Margin = new Thickness(8, 0, 0, 0) }.Styled(buttons[i].Style);
            b.Click += (_, _) => { _result = index; Close(); };
            row.Children.Add(b);
        }
        stack.Children.Add(row);

        var card = new Border
        {
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(22, 20, 22, 18),
            Margin = new Thickness(16), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Luna.Background");
        card.SetResourceReference(Border.BorderBrushProperty, "Luna.Stroke");
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = card;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = -1; Close(); } };
        Loaded += (_, _) => row.Children.OfType<Button>().LastOrDefault()?.Focus();
    }

    /// <summary>Zeigt den Dialog modal und gibt den Index des gewählten Knopfs zurück (-1 = abgebrochen).</summary>
    public static int Show(DependencyObject? context, string title, string message, params DialogButton[] buttons)
    {
        var owner = context != null ? GetWindow(context) : null;
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not LunaDialog);
        var dialog = new LunaDialog(title, message, buttons);
        // Das Popover am Tray (Topmost) versteckt sich, sobald es den Fokus verliert – und ein verstecktes
        // Besitzerfenster nähme den Dialog mit. Dort also ohne Besitzer, aber obenauf.
        if (owner != null && owner.IsVisible && !owner.Topmost)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>Ja/Nein-Rückfrage mit einer (meist zerstörerischen) Aktion und „Abbrechen“.</summary>
    public static bool Confirm(DependencyObject? context, string title, string message, string action, bool destructive = true) =>
        Show(context, title, message,
            new DialogButton(action, destructive ? "Luna.Destructive" : "Luna.Primary"),
            new DialogButton("Abbrechen", "Luna.Secondary", IsDefault: true)) == 0;
}
