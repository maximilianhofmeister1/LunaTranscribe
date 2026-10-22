using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LunaTranscribe.Core;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

/// <summary>
/// Rendert Einstellungen → Datenschutz in voller Höhe (hell und dunkel) als PNG – die Seite ist mit freigegebenem
/// MCP länger als das Fenster von <c>--ui-snapshot</c>. Mit <c>LUNA_SETTINGS_FILE</c> und <c>LUNA_DATA_DIR</c> lässt sich
/// ein Zustand vorgeben, ohne die echte Einrichtung anzufassen.
/// <c>LunaTranscribe.exe --privacy-snapshot [Ordner]</c>
/// </summary>
public static class PrivacySnapshotSelfTest
{
    [SelfTest("--privacy-snapshot")]
    private static async Task<int> Run(string[] args)
    {
        var dir = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "luna-ui"));
        Directory.CreateDirectory(dir);
        var app = Application.Current;
        var state = new AppState(app.Dispatcher, interactive: false);
        foreach (var dark in new[] { false, true })
        {
            LunaTheme.Apply(app.Resources, dark);
            var page = new PrivacySettings(state) { Width = 760 };
            var window = new Window
            {
                Content = new Border { Padding = new Thickness(24), Child = page }.Brushed(Border.BackgroundProperty, "Luna.Background"),
                SizeToContent = SizeToContent.Height, Width = 820, WindowStyle = WindowStyle.None, ShowActivated = false,
                ShowInTaskbar = false, Left = -20000, Top = -20000,
            };
            window.Show();
            // Protokoll und Index-Stand werden im Hintergrund gelesen.
            await Task.Delay(1200);
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            const double scale = 1.5;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                                                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(dir, $"{(dark ? "dunkel" : "hell")}-datenschutz-voll.png");
            await using (var file = File.Create(path)) encoder.Save(file);
            SelfTests.Print("  " + Path.GetFileName(path));
            window.Close();
        }
        LunaTheme.Apply(app.Resources, LunaTheme.SystemUsesDarkMode());
        state.Dispose();
        return 0;
    }
}
