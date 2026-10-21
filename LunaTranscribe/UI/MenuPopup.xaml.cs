using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.UI;

/// <summary>Das Popover unter dem Tray-Symbol (Pendant zu MenuBarView auf macOS).</summary>
public partial class MenuPopup : Window
{
    private readonly AppState _state;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private bool _updating;

    public MenuPopup(AppState state, Action openSettings, Action quit)
    {
        _state = state;
        _openSettings = openSettings;
        _quit = quit;
        InitializeComponent();

        EngineBox.ItemsSource = Enum.GetValues<EngineKind>();
        _state.PropertyChanged += OnStateChanged;
        _state.Models.PropertyChanged += OnStateChanged;
        _state.Settings.PropertyChanged += OnSettingsChanged;
        _state.History.CollectionChanged += OnHistoryChanged;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        RefreshAll();
    }

    public void ShowNearTray()
    {
        RefreshAll();
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

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshStatus);
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshPickers);
    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshHistory);

    private void RefreshAll()
    {
        RefreshPickers();
        RefreshStatus();
        RefreshHistory();
    }

    private void RefreshStatus()
    {
        StatusLabel.Text = _state.StatusText;
        var models = _state.Models.State.Kind;
        StatusDot.Fill = _state.IsRecording ? (Brush)FindResource("RecordingBrush")
            : models == ModelStateKind.Failed ? (Brush)FindResource("RecordingBrush")
            : models == ModelStateKind.Loading ? (Brush)FindResource("TertiaryTextBrush")
            : (Brush)FindResource("OkBrush");

        MicWarning.Visibility = _state.MicrophoneAllowed ? Visibility.Collapsed : Visibility.Visible;
        ToggleButton.Content = _state.IsRecording ? "Aufnahme stoppen" : "Aufnahme starten";
        ToggleButton.IsEnabled = _state.IsRecording || _state.CanStartRecording;
        CancelButton.Visibility = _state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPickers()
    {
        _updating = true;
        try
        {
            var settings = _state.Settings;
            var languages = AppLanguageExtensions.Supported(settings.Engine);
            if (!Equals(LanguageBox.ItemsSource, languages)) LanguageBox.ItemsSource = languages;
            LanguageBox.SelectedItem = languages.Contains(settings.PrimaryLanguage) ? settings.PrimaryLanguage : AppLanguage.English;
            EngineBox.SelectedItem = settings.Engine;
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshHistory()
    {
        var items = _state.History.Take(5).ToList();
        HistoryList.ItemsSource = items;
        HistoryPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LanguageBox.SelectedItem is not AppLanguage l) return;
        _state.Settings.PrimaryLanguage = l;
    }

    private void EngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || EngineBox.SelectedItem is not EngineKind k) return;
        var s = _state.Settings;
        s.Engine = k;
        // Sprachen ggf. auf etwas Unterstütztes zurücksetzen
        if (!s.PrimaryLanguage.IsSupported(k)) s.PrimaryLanguage = AppLanguage.English;
        if (!s.SecondaryLanguage.IsSupported(k)) s.SecondaryLanguage = AppLanguage.English;
        RefreshPickers();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        // Popover schließen, damit der Fokus (und damit Strg+V) wieder in der vorherigen App landet.
        Hide();
        _state.ToggleRecordingFromMenu();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _state.Cancel();

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => _state.ClearHistory();

    private void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Transcript t) return;
        try { Clipboard.SetDataObject(t.Text, true); }
        catch (Exception ex) { Log.Write("copy failed: " + ex.Message); }
    }

    private void OpenMicSettings_Click(object sender, RoutedEventArgs e) => Permissions.OpenMicrophoneSettings();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _openSettings();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    protected override void OnClosing(CancelEventArgs e)
    {
        _state.PropertyChanged -= OnStateChanged;
        _state.Models.PropertyChanged -= OnStateChanged;
        _state.Settings.PropertyChanged -= OnSettingsChanged;
        _state.History.CollectionChanged -= OnHistoryChanged;
        base.OnClosing(e);
    }
}
