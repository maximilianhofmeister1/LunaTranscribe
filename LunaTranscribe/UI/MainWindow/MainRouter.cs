using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.MainWindow;

/// <summary>Seiten des Hauptfensters.</summary>
public enum MainPage { Overview, Meetings, Dictionary, History, Settings }

public static class MainPageExtensions
{
    public static readonly MainPage[] All = { MainPage.Overview, MainPage.Meetings, MainPage.Dictionary, MainPage.History, MainPage.Settings };

    public static string Title(this MainPage p) => p switch
    {
        MainPage.Overview => "Übersicht",
        MainPage.Meetings => "Meetings",
        MainPage.Dictionary => "Wörterbuch",
        MainPage.History => "Verlauf",
        MainPage.Settings => "Einstellungen",
        _ => p.ToString(),
    };

    public static IconKind Icon(this MainPage p) => p switch
    {
        MainPage.Overview => IconKind.MoonStars,
        MainPage.Meetings => IconKind.People,
        MainPage.Dictionary => IconKind.Book,
        MainPage.History => IconKind.History,
        MainPage.Settings => IconKind.Sliders,
        _ => IconKind.None,
    };
}

/// <summary>Bereiche der Einstellungsseite.</summary>
public enum SettingsSection { General, Dictation, Refine, Meetings, Models, Privacy }

public static class SettingsSectionExtensions
{
    public static readonly SettingsSection[] All =
        { SettingsSection.General, SettingsSection.Dictation, SettingsSection.Refine, SettingsSection.Meetings, SettingsSection.Models, SettingsSection.Privacy };

    public static string Title(this SettingsSection s) => s switch
    {
        SettingsSection.General => "Allgemein",
        SettingsSection.Dictation => "Diktat",
        SettingsSection.Refine => "Nachbearbeitung",
        SettingsSection.Meetings => "Meetings",
        SettingsSection.Models => "Modelle",
        SettingsSection.Privacy => "Datenschutz",
        _ => s.ToString(),
    };
}

/// <summary>Navigationszustand des Hauptfensters – von außen steuerbar (Tray-Popover, fertiges Meeting).</summary>
public sealed class MainRouter : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private MainPage _page = MainPage.Overview;
    private Guid? _selectedMeetingId;
    private SettingsSection _settingsSection = SettingsSection.General;

    public MainPage Page
    {
        get => _page;
        set { if (_page == value) return; _page = value; OnPropertyChanged(); }
    }

    public Guid? SelectedMeetingId
    {
        get => _selectedMeetingId;
        set { if (_selectedMeetingId == value) return; _selectedMeetingId = value; OnPropertyChanged(); }
    }

    public SettingsSection SettingsSection
    {
        get => _settingsSection;
        set { if (_settingsSection == value) return; _settingsSection = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
