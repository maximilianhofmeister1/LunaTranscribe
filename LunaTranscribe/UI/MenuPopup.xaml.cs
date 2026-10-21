using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI;

/// <summary>Das Popover über dem Tray-Symbol (Pendant zu MenuBarView auf macOS).</summary>
public partial class MenuPopup : Window
{
    private readonly AppState _state;
    private readonly Action _quit;
    private bool _updating;

    // Feste Bausteine, deren Inhalt sich ändert
    private readonly TextBlock _statusLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Spinner _loading = new() { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly LunaCard _micWarning = new() { Padding = new Thickness(12) };
    private readonly ComboBox _language = new() { MinWidth = 120 };
    private readonly HStack _dictationButtons = new() { Spacing = 6 };
    private readonly VStack _meetingBody = new() { Spacing = 8 };
    private readonly LunaCard _historyCard = new() { Padding = new Thickness(12) };
    private readonly VStack _historyItems = new() { Spacing = 6 };

    public MenuPopup(AppState state, Action quit)
    {
        _state = state;
        _quit = quit;
        InitializeComponent();
        Build();

        _state.PropertyChanged += OnStateChanged;
        _state.Models.PropertyChanged += OnStateChanged;
        _state.Settings.PropertyChanged += OnStateChanged;
        _state.History.CollectionChanged += OnHistoryChanged;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Refresh();
    }

    // MARK: Aufbau

    private void Build()
    {
        // Kopf: Icon, Name, Status
        var header = new DockPanel();
        var icon = new Image { Source = AppIconArt.Image, Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(_loading, Dock.Right);
        header.Children.Add(icon);
        header.Children.Add(_loading);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "LunaTranscribe", FontSize = 18 }.Styled("Luna.Display"));
        _statusLabel.Styled("Luna.Caption");
        _statusLabel.FontSize = 11.5;
        _statusLabel.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(_statusLabel);
        header.Children.Add(titles);
        Body.Children.Add(header);

        // Windows-Pendant zur Bedienungshilfen-Warnung: Mikrofon-Datenschutz
        var warn = new VStack { Spacing = 6 };
        var warnTitle = new StackPanel { Orientation = Orientation.Horizontal };
        warnTitle.Children.Add(new LunaIcon { Kind = IconKind.Warning, Size = 13, Margin = new Thickness(0, 1, 6, 0) }.Brushed(LunaIcon.ForegroundProperty, "Luna.Honey"));
        warnTitle.Children.Add(new TextBlock { Text = "Mikrofonzugriff deaktiviert", FontSize = 12.5, FontWeight = FontWeights.SemiBold }
            .Brushed(TextBlock.ForegroundProperty, "Luna.Honey"));
        warn.Children.Add(warnTitle);
        warn.Children.Add(new TextBlock { Text = "Windows blockiert den Mikrofonzugriff für Desktop-Apps. Ohne ihn kann nichts aufgenommen werden." }.Styled("Luna.Hint"));
        var open = new Button { Content = "Einstellungen öffnen…", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => Permissions.OpenMicrophoneSettings();
        warn.Children.Add(open);
        _micWarning.Content = warn;
        Body.Children.Add(_micWarning);

        // Diktat
        var dictation = new VStack { Spacing = 10 };
        var dictHeader = new DockPanel();
        DockPanel.SetDock(_language, Dock.Right);
        dictHeader.Children.Add(_language);
        dictHeader.Children.Add(new TextBlock { Text = "Diktat", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }.Styled("Luna.Title"));
        _language.ItemTemplate = LanguageTemplate();
        _language.SelectionChanged += (_, _) =>
        {
            if (_updating || _language.SelectedItem is not AppLanguage l) return;
            _state.Settings.PrimaryLanguage = l;
        };
        dictation.Children.Add(dictHeader);
        dictation.Children.Add(_dictationButtons);
        Body.Children.Add(new LunaCard { Padding = new Thickness(12), Content = dictation });

        // Meeting
        var meeting = new VStack { Spacing = 8 };
        meeting.Children.Add(new TextBlock { Text = "Meeting", FontSize = 14 }.Styled("Luna.Title"));
        meeting.Children.Add(_meetingBody);
        Body.Children.Add(new LunaCard { Padding = new Thickness(12), Content = meeting });

        // Zuletzt
        var history = new VStack { Spacing = 6 };
        var histHeader = new DockPanel();
        var all = new Button { Content = "Alle" }.Styled("Luna.Quiet");
        all.Click += (_, _) => { Hide(); _state.OpenMainWindow(MainPage.History); };
        DockPanel.SetDock(all, Dock.Right);
        histHeader.Children.Add(all);
        histHeader.Children.Add(new TextBlock { Text = "Zuletzt", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }.Styled("Luna.Title"));
        history.Children.Add(histHeader);
        history.Children.Add(_historyItems);
        _historyCard.Content = history;
        Body.Children.Add(_historyCard);

        // Fuß
        var footer = new DockPanel { LastChildFill = false };
        var openMain = new Button { Content = "Öffnen" };
        Luna.SetIcon(openMain, IconKind.Window);
        openMain.Click += (_, _) => { Hide(); _state.OpenMainWindow(); };
        var gear = new Button { ToolTip = "Einstellungen", Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(6, 0, 0, 0) };
        Luna.SetIcon(gear, IconKind.Gear);
        gear.Click += (_, _) => { Hide(); _state.OpenSettings(); };
        var quit = new Button { Content = "Beenden" }.Styled("Luna.Quiet");
        quit.Click += (_, _) => _quit();
        DockPanel.SetDock(quit, Dock.Right);
        footer.Children.Add(openMain);
        footer.Children.Add(gear);
        footer.Children.Add(quit);
        Body.Children.Add(footer);
    }

    private static DataTemplate LanguageTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new LanguageNameConverter() });
        return new DataTemplate { VisualTree = factory };
    }

    // MARK: Anzeigen

    public void ShowNearTray()
    {
        Refresh();
        // Erst anzeigen, damit die Größe bekannt ist, dann in der Nähe des Mauszeigers über der Taskleiste platzieren.
        Opacity = 0;
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;
        var w = ActualWidth * dpi.DpiScaleX;
        var h = ActualHeight * dpi.DpiScaleY;

        var x = Math.Clamp(cursor.X - w / 2, wa.Left, wa.Right - w);
        var y = wa.Bottom - h;   // Taskleiste unten (Standard); bei Taskleiste oben:
        if (cursor.Y < wa.Top + 10 || wa.Top > screen.Bounds.Top) y = wa.Top;
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
        Opacity = 1;
        Activate();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.InputLevel)) return;
        Dispatcher.BeginInvoke(Refresh);
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (!IsVisible && IsLoaded) return;   // unsichtbar: beim nächsten Öffnen
        _statusLabel.Text = StatusText();
        _loading.Visibility = _state.Models.State.Kind == ModelStateKind.Loading ? Visibility.Visible : Visibility.Collapsed;
        _micWarning.Visibility = _state.MicrophoneAllowed ? Visibility.Collapsed : Visibility.Visible;
        RefreshLanguage();
        RefreshDictationButtons();
        RefreshMeeting();
        RefreshHistory();
    }

    private void RefreshLanguage()
    {
        _updating = true;
        try
        {
            var settings = _state.Settings;
            var languages = AppLanguageExtensions.Supported(settings.Engine);
            if (_language.ItemsSource is not System.Collections.Generic.List<AppLanguage> current || !current.SequenceEqual(languages))
                _language.ItemsSource = languages;
            _language.SelectedItem = languages.Contains(settings.PrimaryLanguage) ? settings.PrimaryLanguage : AppLanguage.English;
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshDictationButtons()
    {
        _dictationButtons.Children.Clear();
        if (_state.IsRecording)
        {
            var stop = new Button { Content = "Stopp" }.Styled("Luna.Primary");
            Luna.SetIcon(stop, IconKind.Stop);
            stop.Click += (_, _) => { Hide(); _state.ToggleRecordingFromMenu(); };
            _dictationButtons.Children.Add(stop);
        }
        else
        {
            var canDictate = _state.CanStartRecording && !_state.IsBusy;
            var start = new Button { Content = "Starten", IsEnabled = canDictate }.Styled("Luna.Primary");
            Luna.SetIcon(start, IconKind.Mic);
            // Popover schließen, damit der Fokus (und damit Strg+V) wieder in der vorherigen App landet.
            start.Click += (_, _) => { Hide(); _state.ToggleRecordingFromMenu(); };
            var refine = new Button { Content = "Nachbearbeiten", IsEnabled = canDictate, ToolTip = "Diktat wird vom Sprachmodell bereinigt (wie Hotkey 3)" };
            Luna.SetIcon(refine, IconKind.Sparkles);
            refine.Click += (_, _) => { Hide(); _state.ToggleRecordingFromMenu(refine: true); };
            _dictationButtons.Children.Add(start);
            _dictationButtons.Children.Add(refine);
        }
        if (_state.IsBusy)
        {
            var cancel = new Button { ToolTip = "Abbrechen (Esc)" }.Styled("Luna.Quiet");
            Luna.SetIcon(cancel, IconKind.Close);
            cancel.Click += (_, _) => _state.Cancel();
            _dictationButtons.Children.Add(cancel);
        }
    }

    /// <summary>Meeting: Start/Stopp und Laufzeit; darunter die Hintergrund-Verarbeitung beendeter Meetings.</summary>
    private void RefreshMeeting()
    {
        _meetingBody.Children.Clear();
        var start = new Button { Content = "Starten", HorizontalAlignment = HorizontalAlignment.Left };
        Luna.SetIcon(start, IconKind.People);
        start.Click += (_, _) => _state.ToggleMeetingFromMenu();
        _meetingBody.Children.Add(start);
    }

    private void RefreshHistory()
    {
        _historyItems.Children.Clear();
        var items = _state.History.Take(3).ToList();
        _historyCard.Visibility = _state.Settings.KeepHistory && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in items)
        {
            var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            if (t.RawText != null)
            {
                var sparkle = new LunaIcon { Kind = IconKind.Sparkles, Size = 10, Margin = new Thickness(0, 0, 6, 0) }.Brushed(LunaIcon.ForegroundProperty, "Luna.Lavender");
                DockPanel.SetDock(sparkle, Dock.Left);
                row.Children.Add(sparkle);
            }
            var copy = new LunaIcon { Kind = IconKind.Copy, Size = 11, Margin = new Thickness(6, 0, 0, 0) }.Brushed(LunaIcon.ForegroundProperty, "Luna.InkMuted");
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            var text = new TextBlock { Text = t.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }.Brushed(TextBlock.ForegroundProperty, "Luna.Ink");
            row.Children.Add(text);
            row.ToolTip = t.RawText != null ? "Klicken kopiert den Text\n\nRohtext: " + t.RawText : "Klicken kopiert den Text";
            row.MouseLeftButtonUp += (_, _) => UiHelpers.CopyToClipboard(t.Text);
            _historyItems.Children.Add(row);
        }
    }

    private string StatusText()
    {
        if (!_state.Models.IsReady && _state.Models.State.Kind != ModelStateKind.Unloaded && _state.Status == StatusKind.Idle)
            return _state.Models.StatusText;
        return _state.Status switch
        {
            StatusKind.Recording => "Ich höre zu …",
            StatusKind.Transcribing => "Transkribiere …",
            StatusKind.Refining => "Formuliere …",
            StatusKind.Notice => _state.NoticeText,
            _ => _state.MeetingRecording ? "Meeting läuft" : "Bereit · alles lokal",
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _state.PropertyChanged -= OnStateChanged;
        _state.Models.PropertyChanged -= OnStateChanged;
        _state.Settings.PropertyChanged -= OnStateChanged;
        _state.History.CollectionChanged -= OnHistoryChanged;
        base.OnClosing(e);
    }
}
