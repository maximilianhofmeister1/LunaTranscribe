using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LunaTranscribe.Core;
using LunaTranscribe.Audio;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;
using LunaCore;

namespace LunaTranscribe.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppState _state;
    private readonly SettingsStore _settings;
    private bool _updating;

    public SettingsWindow(AppState state)
    {
        _state = state;
        _settings = state.Settings;
        InitializeComponent();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LunaTranscribe.ico"));

        EngineParakeetLabel.Text = EngineKind.Parakeet.Label();
        EngineCohereLabel.Text = EngineKind.Cohere.Label();
        ModelPathText.Text = $"Modelle liegen in {ModelPaths.Base}. Einmaliger Download von Hugging Face; danach arbeitet die App komplett offline.";
        UnloadAfterBox.ItemsSource = UnloadItems;
        VersionLabel.Text = "LunaTranscribe " + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "") + " · © 2026 Maximilian Hofmeister";

        // Solange ein Hotkey-Recorder den Fokus hat, darf der globale Hook die Tasten nicht verschlucken.
        PrimaryRecorder.CapturingChanged += c => _state.HotkeysSuspended = c;
        SecondaryRecorder.CapturingChanged += c => _state.HotkeysSuspended = c;
        PrimaryRecorder.Hotkey = _settings.PrimaryHotkey;
        SecondaryRecorder.Hotkey = _settings.SecondaryHotkey;
        DependencyPropertyDescriptor.FromProperty(HotkeyRecorder.HotkeyProperty, typeof(HotkeyRecorder))
            .AddValueChanged(PrimaryRecorder, (_, _) => { if (!_updating && PrimaryRecorder.Hotkey is { IsSet: true } h) _settings.PrimaryHotkey = h; });
        DependencyPropertyDescriptor.FromProperty(HotkeyRecorder.HotkeyProperty, typeof(HotkeyRecorder))
            .AddValueChanged(SecondaryRecorder, (_, _) => { if (!_updating && SecondaryRecorder.Hotkey is { } h) _settings.SecondaryHotkey = h; });

        LoadDevices();
        RefreshFromSettings();
        RefreshModel();
        RefreshPermissions();

        _settings.PropertyChanged += OnSettingsChanged;
        _state.Models.PropertyChanged += OnModelsChanged;
        _state.PropertyChanged += OnStateChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshFromSettings);
    private void OnModelsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshModel);
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.MicrophoneAllowed)) Dispatcher.BeginInvoke(RefreshPermissions);
    }

    private void RefreshFromSettings()
    {
        _updating = true;
        try
        {
            LaunchAtLogin.IsChecked = _settings.LaunchAtLogin;
            LaunchAtLoginError.Text = _settings.LaunchAtLoginError ?? "";
            LaunchAtLoginError.Visibility = string.IsNullOrEmpty(_settings.LaunchAtLoginError) ? Visibility.Collapsed : Visibility.Visible;

            if (!Equals(PrimaryRecorder.Hotkey, _settings.PrimaryHotkey)) PrimaryRecorder.Hotkey = _settings.PrimaryHotkey;
            if (!Equals(SecondaryRecorder.Hotkey, _settings.SecondaryHotkey)) SecondaryRecorder.Hotkey = _settings.SecondaryHotkey;

            var languages = AppLanguageExtensions.Supported(_settings.Engine);
            if (!Equals(PrimaryLanguage.ItemsSource, languages)) { PrimaryLanguage.ItemsSource = languages; SecondaryLanguage.ItemsSource = languages; }
            PrimaryLanguage.SelectedItem = languages.Contains(_settings.PrimaryLanguage) ? _settings.PrimaryLanguage : AppLanguage.English;
            SecondaryLanguage.SelectedItem = languages.Contains(_settings.SecondaryLanguage) ? _settings.SecondaryLanguage : AppLanguage.English;

            ModeHold.IsChecked = _settings.RecordingMode == RecordingMode.Hold;
            ModeToggle.IsChecked = _settings.RecordingMode == RecordingMode.Toggle;

            PlaySounds.IsChecked = _settings.PlaySounds;
            PauseMedia.IsChecked = _settings.PauseMedia;
            KeepHistory.IsChecked = _settings.KeepHistory;

            UnloadAfterBox.SelectedItem = UnloadItems.FirstOrDefault(u => u.Minutes == _settings.UnloadAfterMinutes)
                ?? UnloadItems.OrderBy(u => Math.Abs(u.Minutes - _settings.UnloadAfterMinutes)).First();
            EngineParakeet.IsChecked = _settings.Engine == EngineKind.Parakeet;
            EngineCohere.IsChecked = _settings.Engine == EngineKind.Cohere;

            SelectDevice(_settings.InputDeviceUid);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RefreshModel()
    {
        var s = _state.Models.State;
        ModelLoading.Visibility = s.Kind == ModelStateKind.Loading ? Visibility.Visible : Visibility.Collapsed;
        ModelReady.Visibility = s.Kind == ModelStateKind.Ready ? Visibility.Visible : Visibility.Collapsed;
        ModelFailed.Visibility = s.Kind == ModelStateKind.Failed ? Visibility.Visible : Visibility.Collapsed;
        ModelIdle.Visibility = s.Kind is ModelStateKind.Idle or ModelStateKind.Unloaded ? Visibility.Visible : Visibility.Collapsed;
        ModelIdle.Text = _state.Models.StatusText;
        switch (s.Kind)
        {
            case ModelStateKind.Loading:
                ModelProgress.Value = s.Progress;
                ModelLoadingText.Text = _state.Models.StatusText;
                break;
            case ModelStateKind.Ready:
                ModelReadyText.Text = $"{s.Engine!.Value.ShortLabel()} bereit";
                break;
            case ModelStateKind.Failed:
                ModelFailedText.Text = s.Error;
                break;
        }
    }

    private void RefreshPermissions()
    {
        var ok = _state.MicrophoneAllowed;
        MicStatus.Text = ok ? "Erteilt" : "Blockiert";
        MicStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "OkBrush" : "RecordingBrush");
    }

    // MARK: Geräte

    private sealed record DeviceItem(string Uid, string Name);
    private sealed record UnloadItem(int Minutes, string Label);

    private static readonly UnloadItem[] UnloadItems =
    {
        new(0, "Nie"), new(2, "nach 2 Minuten"), new(5, "nach 5 Minuten"), new(10, "nach 10 Minuten"),
        new(30, "nach 30 Minuten"), new(60, "nach 1 Stunde"),
    };

    private void UnloadAfter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || UnloadAfterBox.SelectedItem is not UnloadItem u) return;
        _settings.UnloadAfterMinutes = u.Minutes;
    }

    private void LoadDevices()
    {
        var items = new List<DeviceItem>();
        var def = AudioDevices.DefaultInputDevice();
        items.Add(new DeviceItem("", "Systemstandard" + (def != null ? $" ({def.Name})" : "")));
        items.AddRange(AudioDevices.InputDevices().Select(d => new DeviceItem(d.Uid, d.Name)));
        var current = _settings.InputDeviceUid;
        if (!string.IsNullOrEmpty(current) && items.All(i => i.Uid != current))
            items.Add(new DeviceItem(current, "(nicht verbunden)"));
        DeviceBox.ItemsSource = items;
        SelectDevice(current);
    }

    private void SelectDevice(string uid)
    {
        if (DeviceBox.ItemsSource is not List<DeviceItem> items) return;
        DeviceBox.SelectedItem = items.FirstOrDefault(i => i.Uid == uid) ?? items[0];
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _updating = true;
        try { LoadDevices(); } finally { _updating = false; }
    }

    private void DeviceBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || DeviceBox.SelectedItem is not DeviceItem d) return;
        _settings.InputDeviceUid = d.Uid;
    }

    // MARK: Handler

    private void LaunchAtLogin_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _settings.LaunchAtLogin = LaunchAtLogin.IsChecked == true;
        RefreshFromSettings();   // ggf. Fehler anzeigen / Häkchen zurücksetzen
    }

    private void PrimaryLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PrimaryLanguage.SelectedItem is not AppLanguage l) return;
        _settings.PrimaryLanguage = l;
    }

    private void SecondaryLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || SecondaryLanguage.SelectedItem is not AppLanguage l) return;
        _settings.SecondaryLanguage = l;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _settings.RecordingMode = ModeToggle.IsChecked == true ? RecordingMode.Toggle : RecordingMode.Hold;
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _settings.PlaySounds = PlaySounds.IsChecked == true;
        _settings.PauseMedia = PauseMedia.IsChecked == true;
        _settings.KeepHistory = KeepHistory.IsChecked == true;
    }

    private void Engine_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var kind = EngineCohere.IsChecked == true ? EngineKind.Cohere : EngineKind.Parakeet;
        if (kind == _settings.Engine) return;
        _settings.Engine = kind;
        // Sprachen ggf. auf etwas Unterstütztes zurücksetzen
        if (!_settings.PrimaryLanguage.IsSupported(kind)) _settings.PrimaryLanguage = AppLanguage.English;
        if (!_settings.SecondaryLanguage.IsSupported(kind)) _settings.SecondaryLanguage = AppLanguage.English;
    }

    private void RetryModel_Click(object sender, RoutedEventArgs e) => _state.Models.Retry(_settings.Engine);

    private void OpenModelFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ModelPaths.Base);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ModelPaths.Base + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open model folder failed: " + ex.Message); }
    }

    private void OpenMic_Click(object sender, RoutedEventArgs e) => Permissions.OpenMicrophoneSettings();

    protected override void OnClosing(CancelEventArgs e)
    {
        _state.HotkeysSuspended = false;
        _settings.PropertyChanged -= OnSettingsChanged;
        _state.Models.PropertyChanged -= OnModelsChanged;
        _state.PropertyChanged -= OnStateChanged;
        base.OnClosing(e);
    }
}
