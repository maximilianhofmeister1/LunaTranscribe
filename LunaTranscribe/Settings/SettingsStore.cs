using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LunaTranscribe.Engine;
using LunaTranscribe.Hotkeys;
using LunaTranscribe.Output;
using Microsoft.Win32;
using LunaCore;

namespace LunaTranscribe.Settings;

public enum RecordingMode
{
    Hold,    // Taste gedrückt halten = aufnehmen
    Toggle,  // 1× drücken = start, nochmal = stop
}

public static class RecordingModeExtensions
{
    public static string Label(this RecordingMode m) => m == RecordingMode.Hold ? "Gedrückt halten" : "Ein/Aus umschalten";
}

/// <summary>
/// Persistente Einstellungen als JSON in %APPDATA%\LunaTranscribe\settings.json.
/// Enthält keine Audiodaten und keine Transkripte.
/// </summary>
public sealed class SettingsStore : INotifyPropertyChanged
{
    // Reihenfolge wichtig: statische Felder werden in Deklarationsreihenfolge initialisiert,
    // und Load() (für Shared) braucht FilePath und JsonOptions.
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaTranscribe", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static SettingsStore Shared { get; } = Load();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Rohes Speicherformat – bewusst nur Strings/Bools, damit die Datei robust gegen Enum-Umbenennungen bleibt.</summary>
    private sealed class Data
    {
        public string Engine { get; set; } = "parakeet";
        public string PrimaryLanguage { get; set; } = "de";
        public string SecondaryLanguage { get; set; } = "en";
        public string InputDeviceUid { get; set; } = "";
        public string RecordingMode { get; set; } = "hold";
        public bool PlaySounds { get; set; } = true;
        public bool KeepHistory { get; set; } = true;
        public bool PauseMedia { get; set; } = true;
        public int UnloadAfterMinutes { get; set; } = 10;
        public Hotkey PrimaryHotkey { get; set; } = Hotkey.DefaultPrimary;
        public Hotkey SecondaryHotkey { get; set; } = Hotkey.None;
        public string MainWindowPlacement { get; set; } = "";
        public DailyDictationStats DailyDictationStats { get; set; } = new();
    }

    private readonly Data _d;

    private SettingsStore(Data d)
    {
        _d = d;
        _launchAtLogin = ReadLaunchAtLogin();
    }

    private static SettingsStore Load()
    {
        Data? d = null;
        try
        {
            if (File.Exists(FilePath))
                d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new SettingsStore(d ?? new Data());
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_d, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }

    // MARK: Eigenschaften

    public EngineKind Engine
    {
        get => EngineKindExtensions.FromKey(_d.Engine);
        set { if (Engine == value) return; _d.Engine = value.Key(); Changed(); }
    }

    public AppLanguage PrimaryLanguage
    {
        get => AppLanguageExtensions.FromCode(_d.PrimaryLanguage) ?? AppLanguage.German;
        set { if (PrimaryLanguage == value) return; _d.PrimaryLanguage = value.Code(); Changed(); }
    }

    public AppLanguage SecondaryLanguage
    {
        get => AppLanguageExtensions.FromCode(_d.SecondaryLanguage) ?? AppLanguage.English;
        set { if (SecondaryLanguage == value) return; _d.SecondaryLanguage = value.Code(); Changed(); }
    }

    /// <summary>"" = System-Standardmikrofon</summary>
    public string InputDeviceUid
    {
        get => _d.InputDeviceUid;
        set { if (_d.InputDeviceUid == value) return; _d.InputDeviceUid = value ?? ""; Changed(); }
    }

    public RecordingMode RecordingMode
    {
        get => _d.RecordingMode == "toggle" ? RecordingMode.Toggle : RecordingMode.Hold;
        set { if (RecordingMode == value) return; _d.RecordingMode = value == RecordingMode.Toggle ? "toggle" : "hold"; Changed(); }
    }

    public bool PlaySounds
    {
        get => _d.PlaySounds;
        set { if (_d.PlaySounds == value) return; _d.PlaySounds = value; Changed(); }
    }

    public bool KeepHistory
    {
        get => _d.KeepHistory;
        set { if (_d.KeepHistory == value) return; _d.KeepHistory = value; Changed(); }
    }

    public bool PauseMedia
    {
        get => _d.PauseMedia;
        set { if (_d.PauseMedia == value) return; _d.PauseMedia = value; Changed(); }
    }

    /// <summary>Modell nach so vielen Minuten ohne Diktat entladen (spart RAM). 0 = nie.</summary>
    public int UnloadAfterMinutes
    {
        get => Math.Max(0, _d.UnloadAfterMinutes);
        set { if (_d.UnloadAfterMinutes == value) return; _d.UnloadAfterMinutes = Math.Max(0, value); Changed(); }
    }

    public Hotkey PrimaryHotkey
    {
        get => _d.PrimaryHotkey;
        set { if (Equals(_d.PrimaryHotkey, value)) return; _d.PrimaryHotkey = value ?? Hotkey.None; Changed(); }
    }

    public Hotkey SecondaryHotkey
    {
        get => _d.SecondaryHotkey;
        set { if (Equals(_d.SecondaryHotkey, value)) return; _d.SecondaryHotkey = value ?? Hotkey.None; Changed(); }
    }

    /// <summary>Position und Größe des Hauptfensters („x,y,b,h,normal|max“), "" = zentriert.</summary>
    public string MainWindowPlacement
    {
        get => _d.MainWindowPlacement;
        set { if (_d.MainWindowPlacement == value) return; _d.MainWindowPlacement = value ?? ""; Changed(); }
    }

    /// <summary>Tageszähler für die Übersicht – nur Datum und zwei Zahlen, kein Text.</summary>
    public DailyDictationStats DailyDictationStats
    {
        get => _d.DailyDictationStats;
        set { _d.DailyDictationStats = value; Changed(); }
    }

    // MARK: Autostart

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LunaTranscribe";
    private bool _launchAtLogin;
    private string? _launchAtLoginError;

    /// <summary>
    /// Autostart bei der Anmeldung über HKCU\…\Run. Der Zustand lebt in der Registry, nicht in der
    /// JSON-Datei – deshalb wird er hier gelesen und beim Setzen direkt geschrieben.
    /// Erscheint in Einstellungen → Apps → Autostart und im Task-Manager.
    /// </summary>
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (_launchAtLogin == value) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)!;
                if (value) key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
                else key.DeleteValue(RunValue, throwOnMissingValue: false);
                _launchAtLogin = value;
                LaunchAtLoginError = null;
            }
            catch (Exception ex)
            {
                LaunchAtLoginError = ex.Message;
            }
            OnPropertyChanged();
        }
    }

    public string? LaunchAtLoginError
    {
        get => _launchAtLoginError;
        private set { _launchAtLoginError = value; OnPropertyChanged(); }
    }

    private static bool ReadLaunchAtLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    // MARK: Helfer

    /// <summary>Stellt sicher, dass die gewählte Sprache zur Engine passt.</summary>
    public AppLanguage LanguageForEngine(AppLanguage lang) => lang.IsSupported(Engine) ? lang : AppLanguage.English;

    private void Changed([CallerMemberName] string? name = null)
    {
        Save();
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
