using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Settings;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>
/// Die letzten Diktate (nur im Arbeitsspeicher). Kopieren, Rohtext ansehen, korrigieren –
/// Korrekturen werden zu Wörterbuch-Vorschlägen.
/// </summary>
public sealed class HistoryPage : UserControl
{
    private readonly AppState _state;
    private readonly VStack _list = new() { Spacing = 12 };
    private readonly TextBlock _learnedHint = new() { FontWeight = FontWeights.Medium, Cursor = System.Windows.Input.Cursors.Hand, Visibility = Visibility.Collapsed };
    private readonly Button _clear;

    public HistoryPage(AppState state)
    {
        _state = state;
        _clear = new Button { Content = "Verlauf leeren" }.Styled("Luna.Destructive");
        Luna.SetIcon(_clear, IconKind.Trash);
        _clear.Click += (_, _) => _state.ClearHistory();
        _learnedHint.Styled("Luna.Text").Brushed(TextBlock.ForegroundProperty, "Luna.Honey");
        _learnedHint.MouseLeftButtonUp += (_, _) => _state.OpenMainWindow(MainPage.Dictionary);

        var body = new VStack { Spacing = 12 };
        body.Children.Add(_learnedHint);
        body.Children.Add(_list);
        Content = new LunaPage
        {
            Title = "Verlauf",
            Subtitle = $"Die letzten {AppState.HistoryLimit} Diktate – nur im Arbeitsspeicher, nie auf der Platte.",
            Accessory = _clear,
            Content = body,
        };
        Loaded += (_, _) => { _state.History.CollectionChanged += OnHistory; Fill(); };
        Unloaded += (_, _) => _state.History.CollectionChanged -= OnHistory;
    }

    private void OnHistory(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Fill);

    private void Fill()
    {
        _list.Children.Clear();
        _clear.IsEnabled = _state.History.Count > 0;
        if (!_state.Settings.KeepHistory)
        {
            _list.Children.Add(new LunaCard { Content = new TextBlock { Text = "Der Verlauf ist ausgeschaltet (Einstellungen → Datenschutz)." }.Styled("Luna.Hint") });
            return;
        }
        if (_state.History.Count == 0)
        {
            var empty = new VStack { Spacing = 8 };
            empty.Children.Add(new SectionTitle { Title = "Noch still hier", Icon = IconKind.Moon });
            empty.Children.Add(new TextBlock { Text = "Diktiere etwas – es erscheint hier, bis du die App beendest. Ein falsch erkanntes Wort korrigierst du mit dem Stift; daraus wird ein Wörterbuch-Vorschlag." }
                .Styled("Luna.Hint"));
            _list.Children.Add(new LunaCard { Content = empty });
            return;
        }
        foreach (var t in _state.History) _list.Children.Add(Row(t));
    }

    private FrameworkElement Row(Transcript t)
    {
        var stack = new VStack { Spacing = 8 };

        // Kopfzeile: Zeit · Sprache · Wörter in s · [Nachbearbeitet] … [Stift] [Kopieren]
        var header = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var edit = new Button { ToolTip = "Korrigieren – das Wörterbuch lernt daraus" }.Styled("Luna.Quiet");
        Luna.SetIcon(edit, IconKind.Pencil);
        var copy = new Button { ToolTip = "In die Zwischenablage kopieren" }.Styled("Luna.Quiet");
        Luna.SetIcon(copy, IconKind.Copy);
        copy.Click += (_, _) => { UiHelpers.CopyToClipboard(t.Text); Luna.SetIcon(copy, IconKind.Check); };
        actions.Children.Add(edit);
        actions.Children.Add(copy);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var words = DailyDictationStats.CountWords(t.Text);
        meta.Children.Add(new TextBlock { Text = $"{t.Date:HH:mm} · {t.Language.DisplayName()} · {words} Wörter in {t.ProcessingSeconds:0.0} s", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }
            .Styled("Luna.Caption"));
        if (t.RawText != null) meta.Children.Add(new Chip { Text = "Nachbearbeitet", Icon = IconKind.Sparkles, Margin = new Thickness(8, 0, 0, 0) });
        header.Children.Add(meta);
        stack.Children.Add(header);

        var text = new TextBox { Text = t.Text, IsReadOnly = true, FontSize = 14 }.Styled("Luna.PlainTextBox");
        text.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(text);

        if (t.RawText != null)
        {
            stack.Children.Add(new Expander
            {
                Header = new TextBlock { Text = "Rohtext vor der Nachbearbeitung", FontSize = 11.5 }.Styled("Luna.Caption"),
                Content = new TextBox { Text = t.RawText, IsReadOnly = true, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) }.Styled("Luna.PlainTextBox")
                    .Brushed(TextBox.ForegroundProperty, "Luna.InkMuted"),
            }.Brushed(Expander.ForegroundProperty, "Luna.InkMuted"));
        }

        // Bearbeiten
        var editor = new TextBox { MinHeight = 70, FontSize = 14, Visibility = Visibility.Collapsed }.Styled("Luna.Editor");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        var apply = new Button { Content = "Übernehmen & kopieren" }.Styled("Luna.Primary");
        var cancel = new Button { Content = "Abbrechen", Margin = new Thickness(6, 0, 0, 0) }.Styled("Luna.Quiet");
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        stack.Children.Add(editor);
        stack.Children.Add(buttons);

        void SetEditing(bool on)
        {
            editor.Visibility = buttons.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            text.Visibility = actions.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on) { editor.Text = t.Text; editor.Focus(); editor.CaretIndex = editor.Text.Length; }
        }
        edit.Click += (_, _) => SetEditing(true);
        cancel.Click += (_, _) => SetEditing(false);
        apply.Click += (_, _) => Correct(t, editor.Text);

        return new LunaCard { Content = stack };
    }

    private void Correct(Transcript t, string corrected)
    {
        var text = corrected.Trim();
        if (text.Length == 0 || text == t.Text) { Fill(); return; }
        UiHelpers.CopyToClipboard(text);
        var learned = _state.LearnFromCorrection(t.Text, text);
        _state.UpdateHistoryText(t.Id, text);
        _learnedHint.Text = learned == 0 ? "Korrigierter Text kopiert."
            : $"Korrigierter Text kopiert · {VocabularySuggestion.CountLabel(learned)} – ansehen";
        _learnedHint.Visibility = Visibility.Visible;
    }
}
