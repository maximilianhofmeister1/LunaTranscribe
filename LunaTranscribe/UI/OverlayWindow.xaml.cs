using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using LunaTranscribe.Core;

namespace LunaTranscribe.UI;

/// <summary>
/// Kleines schwebendes Fenster am unteren Bildschirmrand, das Aufnahme / Verarbeitung anzeigt.
/// Nimmt nie den Fokus (WS_EX_NOACTIVATE) – sonst würde Strg+V im falschen Fenster landen.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int Bars = 8;
    private readonly AppState _state;
    private readonly Rectangle[] _bars = new Rectangle[Bars];
    private readonly Brush _barOn = new SolidColorBrush(Color.FromRgb(0x4C, 0xD9, 0x64));
    private readonly Brush _barOff = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private readonly DoubleAnimation _spin = new(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
    private bool _shownOnce;

    public OverlayWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        for (var i = 0; i < Bars; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1, Fill = _barOff, Margin = new Thickness(1, 0, 1, 0) };
            LevelMeter.Children.Add(_bars[i]);
        }
        _state.PropertyChanged += OnStateChanged;
        Apply();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.Status):
            case nameof(AppState.NoticeText):
            case nameof(AppState.TranscribingLabel):
                Apply();
                break;
            case nameof(AppState.InputLevel):
                UpdateLevel(_state.InputLevel);
                break;
        }
    }

    private void Apply()
    {
        var status = _state.Status;
        if (status == StatusKind.Idle)
        {
            if (IsVisible) Hide();
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }

        RecordingPanel.Visibility = status == StatusKind.Recording ? Visibility.Visible : Visibility.Collapsed;
        TranscribingPanel.Visibility = status == StatusKind.Transcribing ? Visibility.Visible : Visibility.Collapsed;
        NoticePanel.Visibility = status == StatusKind.Notice ? Visibility.Visible : Visibility.Collapsed;
        NoticeLabel.Text = _state.NoticeText;
        TranscribingLabel.Text = _state.TranscribingLabel;
        if (status == StatusKind.Transcribing) SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _spin);
        else SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        if (status != StatusKind.Recording) UpdateLevel(0);

        ShowNonActivating();
        Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowNonActivating()
    {
        if (!_shownOnce)
        {
            _shownOnce = true;
            Show();   // erstes Show erzeugt das HWND (OnSourceInitialized setzt WS_EX_NOACTIVATE)
        }
        else if (!IsVisible)
        {
            Show();
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Unten mittig auf dem Bildschirm mit dem aktiven Fenster, 60 px über der Taskleiste.</summary>
    private void Reposition()
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow());
        var wa = screen.WorkingArea;
        var w = ActualWidth * dpi.DpiScaleX;
        var h = ActualHeight * dpi.DpiScaleY;
        Left = (wa.Left + (wa.Width - w) / 2) / dpi.DpiScaleX;
        Top = (wa.Bottom - h - 60 * dpi.DpiScaleY) / dpi.DpiScaleY;
    }

    private void UpdateLevel(float level)
    {
        for (var i = 0; i < Bars; i++)
            _bars[i].Fill = (float)i / Bars < level ? _barOn : _barOff;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _state.Cancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        _state.PropertyChanged -= OnStateChanged;
        base.OnClosing(e);
    }

    // MARK: Win32

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
