using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Meeting;
using LunaTranscribe.Settings;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Pages;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI;

/// <summary>
/// Rendert alle Seiten des Hauptfensters, jeden Einstellungsbereich, das Popover und das Overlay in hell und
/// dunkel als PNG – Design prüfen, ohne zu klicken (Pendant zu --ui-snapshot der macOS-Version).
/// <c>LunaTranscribe.exe --ui-snapshot [Ordner]</c>
/// Für die Meetings-Seite zeigt der Test Beispiel-Meetings aus einem temporären Ordner (der eingestellte
/// Meeting-Ordner wird danach wiederhergestellt) und einen wartenden Auftrag in einer temporären Warteschlange.
/// </summary>
public static class UISnapshotSelfTest
{
    [SelfTest("--ui-snapshot")]
    private static async Task<int> Run(string[] args)
    {
        var dir = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "luna-ui"));
        Directory.CreateDirectory(dir);
        var app = Application.Current;

        // Wegwerf-Daten: Warteschlange und Rettungsordner unter einer temporären Basis, Meetings in einem temporären Ordner.
        var temp = Path.Combine(Path.GetTempPath(), "luna-ui-" + Guid.NewGuid().ToString("N"));
        AppPaths.DataDirectoryOverride = Path.Combine(temp, "Daten");
        var settings = SettingsStore.Shared;
        var originalFolder = settings.MeetingFolder;
        // Beispieldaten für Wörterbuch und Verlauf; die echten Einstellungen werden am Ende wiederhergestellt.
        var savedVocabulary = settings.VocabularyState;
        var savedVocabularyEnabled = settings.VocabularyEnabled;
        var savedKeepHistory = settings.KeepHistory;
        try
        {
            settings.KeepHistory = true;
            settings.VocabularyEnabled = true;
            settings.VocabularyState = new VocabularyState(
                new[] { new VocabularyEntry("weclapp", new[] { "Weglap", "Weg Lab" }), new VocabularyEntry("LunaTranscribe", new[] { "Luna Transcribe" }) },
                new[] { new VocabularySuggestion("Kubernetes", "Kuberneedes") { Count = 2 } },
                new System.Collections.Generic.HashSet<string>());
            var state = new AppState(app.Dispatcher, interactive: false);
            SeedHistory(state);
            settings.MeetingFolder = Path.Combine(temp, "Meetings");
            SeedMeetings(state, temp);
            await Snapshot(state, dir);
        }
        finally
        {
            settings.MeetingFolder = originalFolder;
            settings.VocabularyState = savedVocabulary;
            settings.VocabularyEnabled = savedVocabularyEnabled;
            settings.KeepHistory = savedKeepHistory;
            AppPaths.DataDirectoryOverride = null;
            MeetingDetailView.StartOnSummaryTab = false;
            LunaTheme.Apply(app.Resources, LunaTheme.SystemUsesDarkMode());
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
        SelfTests.Print("UI-Snapshots: " + dir);
        return 0;
    }

    private static async Task Snapshot(AppState state, string dir)
    {
        var app = Application.Current;
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
                if (page == MainPage.Meetings)
                {
                    // Dasselbe Meeting mit dem Tab „Zusammenfassung“ (Seite dafür neu erzeugen).
                    MeetingDetailView.StartOnSummaryTab = true;
                    state.Router.Page = MainPage.Overview;
                    state.Router.Page = MainPage.Meetings;
                    await Save(win, Path.Combine(dir, $"{mode}-meetings-zusammenfassung.png"));
                    MeetingDetailView.StartOnSummaryTab = false;
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
    }

    private static void SeedHistory(AppState state)
    {
        if (state.History.Count > 0) return;
        state.History.Add(new Transcript(DateTime.Now.AddMinutes(-2), "Wir treffen uns morgen um elf Uhr zur Planung.", AppLanguage.German, 4.2, 0.3)
            { RawText = "ähm wir treffen uns morgen um um elf uhr zur planung" });
        state.History.Add(new Transcript(DateTime.Now.AddMinutes(-9), "Bitte schick mir den Export aus weclapp.", AppLanguage.German, 3.1, 0.2));
        state.History.Add(new Transcript(DateTime.Now.AddHours(-1), "Let's ship the Windows version this week.", AppLanguage.English, 2.8, 0.2));
    }

    /// <summary>Drei Beispiel-Meetings (eins mit Zusammenfassung, eins vertraulich im Vormonat) und ein wartender Auftrag.</summary>
    private static void SeedMeetings(AppState state, string temp)
    {
        var lines = new[]
        {
            ("Sprecher 1", "Guten Morgen zusammen. Heute besprechen wir den Launch der neuen Version von LunaTranscribe."),
            ("Sprecher 2", "Danke. Ich schlage vor, wir verschieben den Launch auf Montag, weil die Tests noch laufen."),
            ("Sprecher 1", "Einverstanden. Kannst du bis Freitag den Testbericht schicken?"),
            ("Sprecher 2", "Mache ich. Den Export aus weclapp hänge ich gleich mit dran."),
        };
        var turns = lines.Select((l, i) => new MeetingTranscript.Turn(l.Item1, i * 14, i * 14 + 12, l.Item2));
        var launch = new MeetingTranscript(DateTime.UtcNow.AddHours(-1), 58, EngineKind.Parakeet, AppLanguage.German, turns,
            title: "Launch-Planung v0.2", speakerNames: new Dictionary<string, string> { ["Sprecher 1"] = "Anna", ["Sprecher 2"] = "Markus" })
        {
            Summary = new MeetingSummary(string.Join("\n",
                "## Kurzfassung",
                "Der Launch der neuen Version wird auf **Montag** verschoben, weil die Tests noch laufen.",
                "",
                "## Entscheidungen",
                "- Launch am Montag",
                "",
                "## Aufgaben",
                "- [ ] Markus: Testbericht und weclapp-Export schicken (Freitag)",
                "- [x] Anna: Termin im Kalender verschieben"), DateTime.UtcNow, "sonnet"),
        };

        var standup = new MeetingTranscript(DateTime.UtcNow.AddDays(-2), 912, EngineKind.Parakeet, AppLanguage.German,
            new[]
            {
                new MeetingTranscript.Turn("Sprecher 1", 0, 9, "Kurzer Stand: Die Sprechererkennung läuft jetzt auch unter Windows."),
                new MeetingTranscript.Turn("Sprecher 2", 10, 18, "Super, dann teste ich heute Nachmittag mit Teams."),
                new MeetingTranscript.Turn("Sprecher 3", 19, 25, "Ich kümmere mich um die Release Notes."),
            }, title: "Weekly Stand-up");
        var customer = new MeetingTranscript(DateTime.UtcNow.AddMonths(-1), 2710, EngineKind.Cohere, AppLanguage.English,
            new[] { new MeetingTranscript.Turn("Sprecher 1", 0, 12, "Thanks for joining, let's go through the contract details.") },
            title: "Kundentermin Vertrag") { IsConfidential = true };

        state.Meetings.Add(standup);
        state.Meetings.Add(customer);
        if (state.Meetings.Add(launch) is { } entry) state.Router.SelectedMeetingId = entry.Id;

        // Ein wartender Hintergrund-Auftrag (die Warteschlange ist inaktiv, er bleibt also „wartend“).
        Directory.CreateDirectory(temp);
        var wav = Path.Combine(temp, "stille.wav");
        WavReader.WriteMono16k(wav, new float[16_000]);
        state.MeetingQueue.Enqueue(wav, DateTime.UtcNow.AddMinutes(-5), 1834, AppLanguage.German, 0);
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
