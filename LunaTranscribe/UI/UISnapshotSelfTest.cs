using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI;

/// <summary>
/// Rendert alle Seiten des Hauptfensters, jeden Einstellungsbereich, das Popover und das Overlay in hell und
/// dunkel als PNG – Design prüfen, ohne zu klicken (Pendant zu --ui-snapshot der macOS-Version).
/// <c>LunaTranscribe.exe --ui-snapshot [Ordner]</c>
/// </summary>
public static class UISnapshotSelfTest
{
    [SelfTest("--ui-snapshot")]
    private static async Task<int> Run(string[] args)
    {
        var dir = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "luna-ui"));
        Directory.CreateDirectory(dir);
        var app = Application.Current;
        var state = new AppState(app.Dispatcher, interactive: false);
        SeedHistory(state);

        foreach (var dark in new[] { false, true })
        {
            LunaTheme.Apply(app.Resources, dark);
            var mode = dark ? "dunkel" : "hell";

            var win = new MainWindow.MainWindow(state, persistPlacement: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                Width = 1040, Height = 700, ShowActivated = false, ShowInTaskbar = false,
            };
            win.Show();
            foreach (var page in MainPageExtensions.All)
            {
                state.Router.Page = page;
                if (page == MainPage.Settings)
                {
                    foreach (var section in SettingsSectionExtensions.All)
                    {
                        state.Router.SettingsSection = section;
                        await Save(win, Path.Combine(dir, $"{mode}-einstellungen-{section.ToString().ToLowerInvariant()}.png"));
                    }
                }
                else
                {
                    await Save(win, Path.Combine(dir, $"{mode}-{page.ToString().ToLowerInvariant()}.png"));
                }
            }
            win.Close();

            var popup = new MenuPopup(state, () => { }) { Left = -20000, Top = -20000, ShowActivated = false };
            popup.Show();
            await Save(popup, Path.Combine(dir, $"{mode}-popover.png"));
            popup.Close();

            var overlay = new OverlayWindow(state);
            foreach (var (status, name, notice) in new[]
                     {
                         (StatusKind.Recording, "aufnahme", ""), (StatusKind.Transcribing, "transkription", ""),
                         (StatusKind.Refining, "nachbearbeitung", ""), (StatusKind.Notice, "hinweis", "Nichts erkannt."),
                     })
            {
                state.PreviewStatus(status, notice);
                overlay.Left = -20000;
                await Save(overlay, Path.Combine(dir, $"{mode}-overlay-{name}.png"));
            }
            state.PreviewStatus(StatusKind.Idle);
            overlay.Close();
        }
        LunaTheme.Apply(app.Resources, LunaTheme.SystemUsesDarkMode());
        SelfTests.Print("UI-Snapshots: " + dir);
        return 0;
    }

    private static void SeedHistory(AppState state)
    {
        if (state.History.Count > 0) return;
        state.History.Add(new Transcript(DateTime.Now.AddMinutes(-2), "Wir treffen uns morgen um elf Uhr zur Planung.", AppLanguage.German, 4.2, 0.3)
            { RawText = "ähm wir treffen uns morgen um um elf uhr zur planung" });
        state.History.Add(new Transcript(DateTime.Now.AddMinutes(-9), "Bitte schick mir den Export aus weclapp.", AppLanguage.German, 3.1, 0.2));
        state.History.Add(new Transcript(DateTime.Now.AddHours(-1), "Let's ship the Windows version this week.", AppLanguage.English, 2.8, 0.2));
    }

    private static async Task Save(FrameworkElement element, string path)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        const double scale = 1.5;
        var w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
        var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        await using var fs = File.Create(path);
        enc.Save(fs);
        SelfTests.Print("  " + Path.GetFileName(path));
    }
}
