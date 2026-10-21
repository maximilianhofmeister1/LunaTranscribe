using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;
using LunaTranscribe.UI.Components;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>Startseite: Begrüßung, heutige Zahlen, Status der Modelle, Tastenkürzel, letzte Meetings.</summary>
public partial class OverviewPage : UserControl
{
    private readonly AppState _state;
    private readonly LoadStatusView _asrStatus = new();

    public OverviewPage(AppState state)
    {
        _state = state;
        InitializeComponent();
        HeroMoon.Data = LunaIcon.Crescent(30, 42, 27, 13, -10, 22.5);
        var stars = new GeometryGroup();
        stars.Children.Add(LunaIcon.SparklePath(63, 13, 9));
        stars.Children.Add(LunaIcon.SparklePath(68, 37, 5));
        stars.Children.Add(LunaIcon.SparklePath(50, 70, 3.5));
        HeroStars.Data = stars;
        _asrStatus.Retry = () => _state.Models.Retry(_state.Settings.Engine);

        Loaded += (_, _) =>
        {
            _state.PropertyChanged += OnChanged;
            _state.Models.PropertyChanged += OnChanged;
            _state.Refiners.PropertyChanged += OnChanged;
            _state.Settings.PropertyChanged += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _state.PropertyChanged -= OnChanged;
            _state.Models.PropertyChanged -= OnChanged;
            _state.Refiners.PropertyChanged -= OnChanged;
            _state.Settings.PropertyChanged -= OnChanged;
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.InputLevel)) return;
        Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var s = _state.Settings;
        Page.Title = Greeting();

        HeroTitle.Text = _state.Status switch
        {
            StatusKind.Recording => "Ich höre zu …",
            StatusKind.Transcribing or StatusKind.Refining => "Einen Moment …",
            _ => _state.Models.IsReady || _state.Models.State.Kind == Engine.ModelStateKind.Unloaded
                ? "Bereit, wenn du es bist." : "Die Modelle wachen gerade auf …",
        };
        var key = s.PrimaryHotkey.IsSet ? s.PrimaryHotkey.DisplayName : "den Hotkey";
        HeroText.Text = s.RecordingMode == RecordingMode.Hold
            ? $"Halte {key} gedrückt und sprich. Der Text landet dort, wo dein Cursor steht."
            : $"Drück {key}, sprich und drück noch einmal. Der Text landet dort, wo dein Cursor steht.";
        DictateButton.Content = _state.IsRecording ? "Aufnahme stoppen" : "Diktat starten";
        DictateButton.IsEnabled = _state.IsRecording || (_state.CanStartRecording && !_state.IsBusy);
        MeetingButton.Content = _state.MeetingRecording ? "Meeting beenden" : "Meeting starten";

        var today = s.DailyDictationStats.On();
        WordsValue.Text = today.Words.ToString();
        DictationsValue.Text = today.Dictations.ToString();
        MeetingsValue.Text = _state.MeetingCount.ToString();

        // Modelle
        Trim(ModelsCard);
        _asrStatus.Status = _state.Models.ToLoadStatus();
        ModelsCard.Children.Add(Row("Spracherkennung", _asrStatus));
        var refineStatus = new LoadStatusView { Status = _state.Refiners.ToLoadStatus(), Retry = () => _state.Refiners.Retry(_state.RefinerKind) };
        ModelsCard.Children.Add(Row("Nachbearbeitung", refineStatus));
        if (s.VocabularyEnabled && s.VocabularyEntries.Count > 0)
            ModelsCard.Children.Add(Row("Wörterbuch", new LoadStatusView { Status = new LoadStatus(LoadPhase.Ready, _state.VocabularyStatus) }));
        if (!_state.MicrophoneAllowed)
        {
            var allow = new Button { Content = "Mikrofonzugriff erlauben…", HorizontalAlignment = HorizontalAlignment.Left }.Styled("Luna.Destructive");
            allow.Click += (_, _) => Permissions.OpenMicrophoneSettings();
            ModelsCard.Children.Add(allow);
            ModelsCard.Children.Add(new TextBlock { Text = "Windows blockiert den Mikrofonzugriff für Desktop-Apps – ohne ihn kann LunaTranscribe nichts aufnehmen." }.Styled("Luna.Hint"));
        }

        // Tastenkürzel
        Trim(ShortcutsCard);
        ShortcutsCard.Children.Add(Row("Diktat · " + s.PrimaryLanguage.DisplayName(), new Keycap { Text = Display(s.PrimaryHotkey) }));
        ShortcutsCard.Children.Add(Row("Diktat · " + s.SecondaryLanguage.DisplayName(), new Keycap { Text = Display(s.SecondaryHotkey) }));
        ShortcutsCard.Children.Add(Row("Diktat + Nachbearbeitung", new Keycap { Text = Display(s.RefinedHotkey) }));
        ShortcutsCard.Children.Add(Row("Abbrechen", new Keycap { Text = "Esc" }));
        var change = new Button { Content = "Ändern…", HorizontalAlignment = HorizontalAlignment.Left }.Styled("Luna.Quiet");
        change.Click += (_, _) => _state.OpenSettings(SettingsSection.Dictation);
        ShortcutsCard.Children.Add(change);

        // Letzte Meetings
        RecentMeetings.Children.Clear();
        var recent = _state.RecentMeetings;
        if (recent.Count == 0)
        {
            RecentMeetings.Children.Add(new TextBlock
            {
                Text = "Noch keine Meetings. Starte eins über das Symbol im Infobereich – Mikrofon und Systemaudio werden lokal aufgenommen und danach transkribiert.",
            }.Styled("Luna.Hint"));
        }
        foreach (var entry in recent)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(-10, 0, -10, 0),
                Background = Brushes.Transparent, Child = new MeetingRow(entry, _state.Summarizer.IsRunning(entry.Id)),
            };
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Luna.SurfaceMutedHover");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var id = entry.Id;
            row.OnClick(() => _state.OpenMeeting(id));
            RecentMeetings.Children.Add(row);
        }
    }

    private static string Display(Hotkeys.Hotkey hk) => hk.IsSet ? hk.DisplayName : "–";

    /// <summary>Entfernt alles außer dem Titel (erstes Kind).</summary>
    private static void Trim(Panel panel)
    {
        while (panel.Children.Count > 1) panel.Children.RemoveAt(1);
    }

    private static FrameworkElement Row(string label, FrameworkElement content)
    {
        // Beschriftung links, Inhalt rechtsbündig im Rest – lange Statustexte brechen um statt abgeschnitten zu werden.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 12, 0) }.Styled("Luna.TextMuted");
        text.TextWrapping = TextWrapping.NoWrap;
        content.HorizontalAlignment = HorizontalAlignment.Right;
        content.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(content, 1);
        grid.Children.Add(text);
        grid.Children.Add(content);
        return grid;
    }

    private void Dictate_Click(object sender, RoutedEventArgs e) => _state.ToggleRecordingFromMenu();

    private void Meeting_Click(object sender, RoutedEventArgs e) => _state.ToggleMeetingFromMenu();

    private void AllMeetings_Click(object sender, RoutedEventArgs e) => _state.OpenMainWindow(MainPage.Meetings);

    // MARK: Begrüßung

    private static string Greeting()
    {
        var hour = DateTime.Now.Hour;
        var salutation = hour switch
        {
            >= 5 and < 11 => "Guten Morgen",
            >= 11 and < 17 => "Hallo",
            >= 17 and < 22 => "Guten Abend",
            _ => "Gute Nacht",
        };
        var first = FirstName();
        return string.IsNullOrEmpty(first) ? salutation : $"{salutation}, {first}";
    }

    private static string? _firstName;

    /// <summary>Vorname aus dem vollständigen Namen des Windows-Kontos; sonst der Kontoname.</summary>
    private static string FirstName()
    {
        if (_firstName != null) return _firstName;
        string full = "";
        try
        {
            if (NetUserGetInfo(null, Environment.UserName, 10, out var buf) == 0 && buf != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<USER_INFO_10>(buf);
                full = info.usri10_full_name ?? "";
                NetApiBufferFree(buf);
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(full)) full = Environment.UserName;
        var first = full.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[0] : "";
        if (first.Length > 0) first = char.ToUpper(first[0]) + first[1..];
        return _firstName = first;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_10
    {
        public string usri10_name;
        public string usri10_comment;
        public string usri10_usr_comment;
        public string usri10_full_name;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buf);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);
}
