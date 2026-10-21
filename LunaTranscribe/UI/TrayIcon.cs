using System;
using System.ComponentModel;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace LunaTranscribe.UI;

/// <summary>
/// Das Symbol im Infobereich (Pendant zur macOS-Menüleiste). Linksklick öffnet das Popover,
/// Rechtsklick ein kompaktes Kontextmenü. Das Symbol zeigt den Zustand (Aufnahme, Transkription, Laden).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppState _state;
    private readonly Action _quit;
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu;
    private readonly WF.ToolStripMenuItem _toggleItem;
    private readonly WF.ToolStripMenuItem _cancelItem;
    private MenuPopup? _popup;
    private TrayIconKind _currentKind = (TrayIconKind)(-1);
    private bool _lightTaskbar;

    public TrayIcon(AppState state, Action quit)
    {
        _state = state;
        _quit = quit;
        _lightTaskbar = TaskbarIsLight();

        _toggleItem = new WF.ToolStripMenuItem("Aufnahme starten", null, (_, _) => _state.ToggleRecordingFromMenu());
        _cancelItem = new WF.ToolStripMenuItem("Abbrechen (Esc)", null, (_, _) => _state.Cancel());
        _menu = new WF.ContextMenuStrip();
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(_cancelItem);
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add(new WF.ToolStripMenuItem("LunaTranscribe öffnen", null, (_, _) => _state.OpenMainWindow()));
        _menu.Items.Add(new WF.ToolStripMenuItem("Einstellungen…", null, (_, _) => _state.OpenSettings()));
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add(new WF.ToolStripMenuItem("Beenden", null, (_, _) => _quit()));
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new WF.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "LunaTranscribe",
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) TogglePopup();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) { _popup?.Hide(); _state.OpenMainWindow(); }
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _notificationClick;
            _notificationClick = null;
            action?.Invoke();
        };

        _state.PropertyChanged += OnStateChanged;
        _state.Models.PropertyChanged += OnStateChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Refresh();
    }

    private Action? _notificationClick;

    /// <summary>
    /// Mitteilung über das Infobereich-Symbol (unter Windows 10/11 eine Benachrichtigung im Info-Center).
    /// <paramref name="onClick"/> läuft, wenn der Nutzer darauf klickt; eine neue Mitteilung ersetzt die alte Aktion.
    /// </summary>
    public void ShowNotification(string title, string text, Action? onClick)
    {
        _notificationClick = onClick;
        // Windows begrenzt Titel auf 63 und Text auf 255 Zeichen.
        var t = title.Length > 63 ? title[..62] + "…" : title;
        var body = text.Length > 255 ? text[..254] + "…" : text;
        _icon.ShowBalloonTip(10_000, t, body, WF.ToolTipIcon.None);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var light = TaskbarIsLight();
        if (light == _lightTaskbar) return;
        _lightTaskbar = light;
        _currentKind = (TrayIconKind)(-1);
        Refresh();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var kind = _state.Status switch
        {
            StatusKind.Recording => TrayIconKind.Recording,
            StatusKind.Transcribing => TrayIconKind.Transcribing,
            StatusKind.Refining => TrayIconKind.Refining,
            // Dahinter zeigt das Symbol ein laufendes Meeting bzw. die Hintergrund-Verarbeitung (wie die macOS-Menüleiste).
            _ when _state.MeetingRecording => TrayIconKind.Meeting,
            _ when _state.MeetingQueue.IsProcessing => TrayIconKind.Transcribing,
            _ when _state.Models.State.Kind == ModelStateKind.Loading => TrayIconKind.Loading,
            _ when _state.Models.State.Kind == ModelStateKind.Failed => TrayIconKind.Failed,
            _ => TrayIconKind.Idle,
        };
        if (kind != _currentKind)
        {
            _currentKind = kind;
            var old = _icon.Icon;
            _icon.Icon = TrayIcons.Render(kind, _lightTaskbar);
            old?.Dispose();
        }

        // Tooltip max. 63 Zeichen
        var text = "LunaTranscribe – " + _state.StatusText;
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text;
    }

    private void RefreshMenu()
    {
        _toggleItem.Text = _state.IsRecording ? "Aufnahme stoppen" : "Aufnahme starten";
        _toggleItem.Enabled = _state.IsRecording || _state.CanStartRecording;
        _cancelItem.Visible = _state.IsBusy;
    }

    private void TogglePopup()
    {
        _popup ??= new MenuPopup(_state, _quit);
        if (_popup.IsVisible) _popup.Hide();
        else _popup.ShowNearTray();
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int i && i == 1;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _state.PropertyChanged -= OnStateChanged;
        _state.Models.PropertyChanged -= OnStateChanged;
        _popup?.Close();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}

public enum TrayIconKind { Idle, Recording, Transcribing, Refining, Meeting, Loading, Failed }

/// <summary>Zeichnet die Tray-Symbole zur Laufzeit (Wellenform-Glyphe in Schwarz/Weiß je nach Taskleisten-Design).</summary>
public static class TrayIcons
{
    public static SD.Icon Render(TrayIconKind kind, bool lightTaskbar)
    {
        var size = Math.Max(16, WF.SystemInformation.SmallIconSize.Width);
        // Intern doppelt so groß zeichnen und herunterskalieren → glattere Kanten.
        const int scale = 4;
        var s = size * scale;
        using var big = new SD.Bitmap(s, s, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(big))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(SD.Color.Transparent);
            var fg = lightTaskbar ? SD.Color.FromArgb(20, 20, 20) : SD.Color.White;
            var muted = lightTaskbar ? SD.Color.FromArgb(120, 120, 120) : SD.Color.FromArgb(180, 180, 180);
            var red = SD.Color.FromArgb(229, 57, 53);
            var honey = SD.Color.FromArgb(0xE8, 0xB0, 0x4B);

            switch (kind)
            {
                case TrayIconKind.Idle:
                    DrawWave(g, s, fg);
                    break;
                case TrayIconKind.Recording:
                    DrawWave(g, s, red);
                    break;
                case TrayIconKind.Transcribing:
                    DrawDots(g, s, fg);
                    break;
                case TrayIconKind.Refining:
                    DrawSparkles(g, s, fg);
                    break;
                case TrayIconKind.Meeting:
                    DrawWave(g, s, fg);
                    DrawRecordBadge(g, s, honey);
                    break;
                case TrayIconKind.Loading:
                    DrawWave(g, s, muted);
                    DrawBadge(g, s, fg, down: true);
                    break;
                case TrayIconKind.Failed:
                    DrawWave(g, s, muted);
                    DrawBadge(g, s, red, down: false);
                    break;
            }
        }
        using var small = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(small))
        {
            g.InterpolationMode = SD.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(big, new SD.Rectangle(0, 0, size, size));
        }
        var handle = small.GetHicon();
        try
        {
            // Icon-Kopie erzeugen, damit das GDI-Handle sauber freigegeben werden kann.
            using var tmp = SD.Icon.FromHandle(handle);
            return (SD.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawWave(SD.Graphics g, int s, SD.Color color)
    {
        float[] heights = { 0.28f, 0.55f, 0.85f, 0.45f, 0.65f, 0.30f };
        var barW = s * 0.10f;
        var gap = s * 0.06f;
        var total = heights.Length * barW + (heights.Length - 1) * gap;
        var x = (s - total) / 2;
        using var brush = new SD.SolidBrush(color);
        foreach (var h in heights)
        {
            var bh = h * s * 0.9f;
            var y = (s - bh) / 2;
            FillRounded(g, brush, x, y, barW, bh, barW / 2);
            x += barW + gap;
        }
    }

    /// <summary>Drei vierzackige Sterne (Nachbearbeitung, wie das SF-Symbol „sparkles“).</summary>
    private static void DrawSparkles(SD.Graphics g, int s, SD.Color color)
    {
        using var brush = new SD.SolidBrush(color);
        void Star(float cx, float cy, float r)
        {
            var i = r * 0.28f;
            using var path = new SD.Drawing2D.GraphicsPath();
            path.AddBezier(cx, cy - r, cx + i, cy - i, cx + i, cy - i, cx + r, cy);
            path.AddBezier(cx + r, cy, cx + i, cy + i, cx + i, cy + i, cx, cy + r);
            path.AddBezier(cx, cy + r, cx - i, cy + i, cx - i, cy + i, cx - r, cy);
            path.AddBezier(cx - r, cy, cx - i, cy - i, cx - i, cy - i, cx, cy - r);
            g.FillPath(brush, path);
        }
        Star(s * 0.42f, s * 0.58f, s * 0.34f);
        Star(s * 0.78f, s * 0.22f, s * 0.15f);
        Star(s * 0.82f, s * 0.78f, s * 0.10f);
    }

    /// <summary>Aufnahme-Punkt unten rechts (laufendes Meeting).</summary>
    private static void DrawRecordBadge(SD.Graphics g, int s, SD.Color color)
    {
        var r = s * 0.19f;
        var cx = s - r - s * 0.03f;
        var cy = s - r - s * 0.03f;
        using var brush = new SD.SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
    }

    private static void DrawDots(SD.Graphics g, int s, SD.Color color)
    {
        using var brush = new SD.SolidBrush(color);
        var r = s * 0.11f;
        var gap = s * 0.10f;
        var total = 3 * 2 * r + 2 * gap;
        var x = (s - total) / 2;
        for (var i = 0; i < 3; i++)
        {
            g.FillEllipse(brush, x, s / 2f - r, 2 * r, 2 * r);
            x += 2 * r + gap;
        }
    }

    private static void DrawBadge(SD.Graphics g, int s, SD.Color color, bool down)
    {
        var r = s * 0.20f;
        var cx = s - r - s * 0.04f;
        var cy = s - r - s * 0.04f;
        using var brush = new SD.SolidBrush(color);
        g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
        using var pen = new SD.Pen(SD.Color.FromArgb(30, 30, 30), s * 0.05f) { StartCap = SD.Drawing2D.LineCap.Round, EndCap = SD.Drawing2D.LineCap.Round };
        if (down)
        {
            // Pfeil nach unten
            g.DrawLine(pen, cx, cy - r * 0.5f, cx, cy + r * 0.45f);
            g.DrawLine(pen, cx - r * 0.4f, cy + r * 0.05f, cx, cy + r * 0.45f);
            g.DrawLine(pen, cx + r * 0.4f, cy + r * 0.05f, cx, cy + r * 0.45f);
        }
        else
        {
            // Ausrufezeichen
            pen.Color = SD.Color.White;
            g.DrawLine(pen, cx, cy - r * 0.5f, cx, cy + r * 0.1f);
            g.DrawLine(pen, cx, cy + r * 0.45f, cx, cy + r * 0.5f);
        }
    }

    private static void FillRounded(SD.Graphics g, SD.Brush brush, float x, float y, float w, float h, float radius)
    {
        using var path = new SD.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
