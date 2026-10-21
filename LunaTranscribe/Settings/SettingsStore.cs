using System;
using System.Collections.Generic;
using System.Linq;
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
    // LUNA_SETTINGS_FILE lenkt nur für Selbsttests/Snapshots auf eine andere Datei um (wie bei luna-mcp).
    private static readonly string FilePath =
        Environment.GetEnvironmentVariable("LUNA_SETTINGS_FILE") is { Length: > 0 } testPath
            ? testPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaTranscribe", "settings.json");

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
        public Hotkey RefinedHotkey { get; set; } = Hotkey.None;
        public string MainWindowPlacement { get; set; } = "";
        public DailyDictationStats DailyDictationStats { get; set; } = new();

        // Meetings (Schlüssel, die luna-mcp liest, stehen in LunaCore.SharedSettings.Key – nie umbenennen)
        public bool MeetingCaptureSystemAudio { get; set; } = true;
        public bool MeetingKeepAudio { get; set; }
        public string MeetingFolder { get; set; } = "";
        public int MeetingSpeakerCount { get; set; }
        public string ClaudeCliPath { get; set; } = "";
        public string ClaudeModel { get; set; } = "";
        public string SummaryPrompt { get; set; } = "";
        public bool SummaryCloudNoticeAccepted { get; set; }

        // Nachbearbeitung
        public string RefinerKind { get; set; } = "";
        public string RefinePrompt { get; set; } = "";

        // Wörterbuch
        public bool VocabularyEnabled { get; set; } = true;
        public List<VocabularyEntry> VocabularyEntries { get; set; } = new();
        public List<VocabularySuggestion> VocabularySuggestions { get; set; } = new();
        public List<string> IgnoredSuggestionKeys { get; set; } = new();

        // Claude (luna-mcp)
        public bool McpAccessEnabled { get; set; }
        public bool McpWriteEnabled { get; set; }
        public bool McpConfirmRenames { get; set; }
        public bool McpCloudNoticeAccepted { get; set; }
        public bool SemanticSearchEnabled { get; set; }
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

    /// <summary>Dritter Hotkey: Hauptsprache + Nachbearbeitung. Kein Standard (wie macOS).</summary>
    public Hotkey RefinedHotkey
    {
        get => _d.RefinedHotkey;
        set { if (Equals(_d.RefinedHotkey, value)) return; _d.RefinedHotkey = value ?? Hotkey.None; Changed(); }
    }

    // MARK: Meetings

    /// <summary>Systemaudio (Wiedergabe von Teams, Zoom, Browser …) mitschneiden.</summary>
    public bool MeetingCaptureSystemAudio { get => _d.MeetingCaptureSystemAudio; set => Set(_d.MeetingCaptureSystemAudio, value, v => _d.MeetingCaptureSystemAudio = v); }

    /// <summary>Aufnahme (audio.wav) im Meeting-Ordner behalten.</summary>
    public bool MeetingKeepAudio { get => _d.MeetingKeepAudio; set => Set(_d.MeetingKeepAudio, value, v => _d.MeetingKeepAudio = v); }

    /// <summary>Meeting-Ordner; "" = Dokumente\LunaTranscribe Meetings.</summary>
    public string MeetingFolder { get => _d.MeetingFolder; set => Set(_d.MeetingFolder, value ?? "", v => _d.MeetingFolder = v); }

    /// <summary>Erwartete Teilnehmerzahl für die Sprechererkennung; 0 = automatisch.</summary>
    public int MeetingSpeakerCount { get => _d.MeetingSpeakerCount; set => Set(_d.MeetingSpeakerCount, Math.Clamp(value, 0, 8), v => _d.MeetingSpeakerCount = v); }

    /// <summary>Eigener Pfad zur Claude CLI; "" = automatisch suchen.</summary>
    public string ClaudeCliPath { get => _d.ClaudeCliPath; set => Set(_d.ClaudeCliPath, value ?? "", v => _d.ClaudeCliPath = v); }

    /// <summary>Modell für Zusammenfassungen (z. B. „sonnet“); "" = Standard der CLI.</summary>
    public string ClaudeModel { get => _d.ClaudeModel; set => Set(_d.ClaudeModel, value ?? "", v => _d.ClaudeModel = v); }

    /// <summary>Anweisung für Zusammenfassungen; gespeichert wird nur eine Abweichung vom Standard ("" = Standard).</summary>
    public string SummaryPrompt
    {
        get => string.IsNullOrWhiteSpace(_d.SummaryPrompt) ? LunaCore.SummaryPrompt.DefaultTemplate : _d.SummaryPrompt;
        set => Set(_d.SummaryPrompt, value == LunaCore.SummaryPrompt.DefaultTemplate ? "" : value ?? "", v => _d.SummaryPrompt = v);
    }

    public bool SummaryPromptIsDefault => string.IsNullOrWhiteSpace(_d.SummaryPrompt);

    public bool SummaryCloudNoticeAccepted { get => _d.SummaryCloudNoticeAccepted; set => Set(_d.SummaryCloudNoticeAccepted, value, v => _d.SummaryCloudNoticeAccepted = v); }

    // MARK: Nachbearbeitung

    /// <summary>Schlüssel des Sprachmodells (RefinerKind); "" = Standard.</summary>
    public string RefinerKey { get => _d.RefinerKind; set => Set(_d.RefinerKind, value ?? "", v => _d.RefinerKind = v, nameof(RefinerKey)); }

    /// <summary>Eigene Anweisung für die Nachbearbeitung; "" = mitgelieferte Fassung.</summary>
    public string RefinePromptOverride { get => _d.RefinePrompt; set => Set(_d.RefinePrompt, value ?? "", v => _d.RefinePrompt = v, nameof(RefinePromptOverride)); }

    // MARK: Wörterbuch

    public bool VocabularyEnabled { get => _d.VocabularyEnabled; set => Set(_d.VocabularyEnabled, value, v => _d.VocabularyEnabled = v); }

    /// <summary>Einträge (Kopie). Ändern nur über den Setter, damit gespeichert und benachrichtigt wird.</summary>
    public List<VocabularyEntry> VocabularyEntries
    {
        get => _d.VocabularyEntries.Select(e => e.Clone()).ToList();
        set { _d.VocabularyEntries = value?.Select(e => e.Clone()).ToList() ?? new(); Changed(); }
    }

    public List<VocabularySuggestion> VocabularySuggestions
    {
        get => _d.VocabularySuggestions.Select(e => e with { }).ToList();
        set { _d.VocabularySuggestions = value?.ToList() ?? new(); Changed(); }
    }

    public List<string> IgnoredSuggestionKeys
    {
        get => _d.IgnoredSuggestionKeys.ToList();
        set { _d.IgnoredSuggestionKeys = value?.Distinct().ToList() ?? new(); Changed(); }
    }

    /// <summary>Wörterbuch-Zustand für LunaCore.VocabularyLearner.</summary>
    public VocabularyState VocabularyState
    {
        get => new(VocabularyEntries, VocabularySuggestions, IgnoredSuggestionKeys.ToHashSet());
        set
        {
            _d.VocabularyEntries = value.Entries.Select(e => e.Clone()).ToList();
            _d.VocabularySuggestions = value.Suggestions.ToList();
            _d.IgnoredSuggestionKeys = value.IgnoredKeys.ToList();
            Save();
            OnPropertyChanged(nameof(VocabularyEntries));
            OnPropertyChanged(nameof(VocabularySuggestions));
            OnPropertyChanged(nameof(IgnoredSuggestionKeys));
        }
    }

    // MARK: Claude (luna-mcp)

    public bool McpAccessEnabled { get => _d.McpAccessEnabled; set => Set(_d.McpAccessEnabled, value, v => _d.McpAccessEnabled = v); }
    public bool McpWriteEnabled { get => _d.McpWriteEnabled; set => Set(_d.McpWriteEnabled, value, v => _d.McpWriteEnabled = v); }
    public bool McpConfirmRenames { get => _d.McpConfirmRenames; set => Set(_d.McpConfirmRenames, value, v => _d.McpConfirmRenames = v); }
    public bool McpCloudNoticeAccepted { get => _d.McpCloudNoticeAccepted; set => Set(_d.McpCloudNoticeAccepted, value, v => _d.McpCloudNoticeAccepted = v); }
    public bool SemanticSearchEnabled { get => _d.SemanticSearchEnabled; set => Set(_d.SemanticSearchEnabled, value, v => _d.SemanticSearchEnabled = v); }

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

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        Changed(name);
    }

    private void Changed([CallerMemberName] string? name = null)
    {
        Save();
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
