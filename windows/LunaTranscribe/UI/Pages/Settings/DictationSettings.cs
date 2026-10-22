using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LunaCore;
using LunaTranscribe.Audio;
using LunaTranscribe.Core;
using LunaTranscribe.Hotkeys;
using LunaTranscribe.Settings;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

public sealed class DictationSettings : SettingsSectionView
{
    private List<AudioInputDevice> _devices = AudioDevices.InputDevices();

    public DictationSettings(AppState state) : base(state) { }

    protected override void Build(VStack root)
    {
        var s = State.Settings;
        root.Children.Add(Card("Tastenkürzel & Sprachen", IconKind.Keyboard,
            Row("Diktieren", null, Recorder(s.PrimaryHotkey, v => s.PrimaryHotkey = v, allowEmpty: false),
                LanguagePicker(s.PrimaryLanguage, v => s.PrimaryLanguage = v)),
            Row("Diktieren in Zweitsprache", null, Recorder(s.SecondaryHotkey, v => s.SecondaryHotkey = v),
                LanguagePicker(s.SecondaryLanguage, v => s.SecondaryLanguage = v)),
            Row("Diktieren + Nachbearbeitung", "Hauptsprache; ein lokales Sprachmodell bereinigt den Text.",
                Recorder(s.RefinedHotkey, v => s.RefinedHotkey = v)),
            Row("Verhalten", null, Segmented(new[] { RecordingMode.Hold, RecordingMode.Toggle }, s.RecordingMode, m => m.Label(), v => s.RecordingMode = v)),
            new LearnMore
            {
                Text = "LunaTranscribe fängt die Hotkey-Taste ab – sie kommt im aktiven Programm nicht an (F2 löst also z. B. in Excel " +
                       "nicht mehr „Zelle bearbeiten“ aus). Ins Feld klicken und die gewünschte Kombination drücken; Rücktaste entfernt den Hotkey. " +
                       "Escape bricht ein laufendes Diktat ab. In Programme, die als Administrator laufen, kann nur eingefügt werden, " +
                       "wenn auch LunaTranscribe als Administrator läuft.",
            }));

        var deviceItems = new List<(string Uid, string Name)> { ("", "Systemstandard" + DefaultSuffix()) };
        deviceItems.AddRange(_devices.Select(d => (d.Uid, d.Name)));
        if (!string.IsNullOrEmpty(s.InputDeviceUid) && _devices.All(d => d.Uid != s.InputDeviceUid))
            deviceItems.Add((s.InputDeviceUid, "Gewähltes Mikrofon (nicht verbunden)"));
        var current = deviceItems.FirstOrDefault(d => d.Uid == s.InputDeviceUid);
        root.Children.Add(Card("Audio", IconKind.Mic,
            Row("Mikrofon", null,
                Picker(deviceItems, current, d => d.Name, d => s.InputDeviceUid = d.Uid, width: 260),
                IconButton(IconKind.Refresh, "Geräte aktualisieren", () => { _devices = AudioDevices.InputDevices(); Rebuild(); })),
            ToggleRow("Musik und Videos während der Aufnahme pausieren", "Setzt nur fort, was LunaTranscribe selbst pausiert hat.",
                s.PauseMedia, v => s.PauseMedia = v)));
    }

    private FrameworkElement LanguagePicker(AppLanguage selected, System.Action<AppLanguage> set) =>
        Picker(AppLanguageExtensions.Supported(State.Settings.Engine), selected, l => l.DisplayName(), set, width: 150);

    private FrameworkElement Recorder(Hotkey value, System.Action<Hotkey> set, bool allowEmpty = true)
    {
        var recorder = new HotkeyRecorder { Hotkey = value, AllowEmpty = allowEmpty, Width = 180 };
        recorder.CapturingChanged += capturing => State.HotkeysSuspended = capturing;
        recorder.HotkeyChanged += hk => Quietly(() => set(hk));
        return recorder;
    }

    private static string DefaultSuffix() => AudioDevices.DefaultInputDevice() is { } d ? $" ({d.Name})" : "";
}
