using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LunaTranscribe.Core;
using LunaTranscribe.UI.Pages;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.MainWindow;

/// <summary>
/// Hauptfenster: Seitenleiste + aktuelle Seite (Pendant zu MainView/MainWindowController der macOS-Version).
/// Eigene Titelleiste wie die transparente macOS-Titelleiste; Größe und Position bleiben über Neustarts erhalten.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppState _state;
    private MainRouter Router => _state.Router;
    private readonly RadioButton[] _navItems;

    /// <summary>false für UI-Snapshots: Position nicht übernehmen und nicht speichern.</summary>
    public bool PersistPlacement { get; init; } = true;

    public MainWindow(AppState state, bool persistPlacement = true)
    {
        PersistPlacement = persistPlacement;
        _state = state;
        InitializeComponent();
        Logo.Source = AppIconArt.Image;
        Icon = AppIconArt.Image;

        _navItems = new RadioButton[MainPageExtensions.All.Length];
        for (var i = 0; i < _navItems.Length; i++)
        {
            var page = MainPageExtensions.All[i];
            var item = new RadioButton { Content = page.Title(), GroupName = "Nav", Tag = page };
            item.SetResourceReference(StyleProperty, "Luna.SidebarItem");
            Luna.SetIcon(item, page.Icon());
            item.Checked += (_, _) => Router.Page = page;
            _navItems[i] = item;
            Nav.Children.Add(item);
        }

        ModelStatus.Retry = () => _state.Models.Retry(_state.Settings.Engine);
        Router.PropertyChanged += OnRouterChanged;
        _state.PropertyChanged += OnStateChanged;
        _state.Models.PropertyChanged += OnStateChanged;
        if (PersistPlacement) RestorePlacement();
        ShowPage();
        RefreshFooter();
        StateChanged += (_, _) => ApplyMaximized();
        Closing += (_, _) => { if (PersistPlacement) SavePlacement(); };
        Closed += (_, _) =>
        {
            Router.PropertyChanged -= OnRouterChanged;
            _state.PropertyChanged -= OnStateChanged;
            _state.Models.PropertyChanged -= OnStateChanged;
        };
    }

    // MARK: Navigation

    private void OnRouterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainRouter.Page)) ShowPage();
    }

    private void ShowPage()
    {
        foreach (var item in _navItems) item.IsChecked = (MainPage)item.Tag == Router.Page;
        PageHost.Content = CreatePage(Router.Page);
    }

    /// <summary>Seiten werden bei jedem Wechsel neu erzeugt (wie SwiftUI) und lösen ihre Abos in Unloaded.</summary>
    private FrameworkElement CreatePage(MainPage page) => page switch
    {
        MainPage.Overview => new OverviewPage(_state),
        _ => new PlaceholderPage(page.Title()),
    };

    // MARK: Fußzeile

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshFooter);

    private void RefreshFooter()
    {
        ModelStatus.Status = _state.Models.ToLoadStatus();
        var badgeCount = _state.SuggestionCount;
        foreach (var item in _navItems)
            if ((MainPage)item.Tag == MainPage.Dictionary) Luna.SetBadge(item, badgeCount);

        MeetingLine.Visibility = _state.MeetingRecording ? Visibility.Visible : Visibility.Collapsed;
        MeetingText.Text = "Meeting läuft · " + _state.MeetingElapsedText;
        var pending = _state.MeetingsPending;
        QueueLine.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueText.Text = pending == 1 ? "1 Meeting in Verarbeitung" : $"{pending} Meetings in Verarbeitung";
    }

    // MARK: Titelleiste

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyMaximized()
    {
        // Mit WindowChrome ragt ein maximiertes Fenster um die Rahmenbreite über den Bildschirm hinaus.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Wiederherstellen" : "Maximieren";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Windows 11: abgerundete Ecken auch mit eigener Titelleiste.
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ApplyMaximized();
    }

    // MARK: Position merken

    private void RestorePlacement()
    {
        var p = _state.Settings.MainWindowPlacement;
        var parts = p.Split(',');
        if (parts.Length < 4) return;
        var nums = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out nums[i])) return;
        var rect = new Rect(nums[0], nums[1], Math.Max(MinWidth, nums[2]), Math.Max(MinHeight, nums[3]));
        // Nur übernehmen, wenn das Fenster noch auf einem vorhandenen Bildschirm liegt.
        var visible = rect.IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
        if (!visible) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.X; Top = rect.Y; Width = rect.Width; Height = rect.Height;
        if (parts.Length > 4 && parts[4] == "max") WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _state.Settings.MainWindowPlacement = string.Join(",",
            r.X.ToString("0", CultureInfo.InvariantCulture), r.Y.ToString("0", CultureInfo.InvariantCulture),
            r.Width.ToString("0", CultureInfo.InvariantCulture), r.Height.ToString("0", CultureInfo.InvariantCulture),
            WindowState == WindowState.Maximized ? "max" : "normal");
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
