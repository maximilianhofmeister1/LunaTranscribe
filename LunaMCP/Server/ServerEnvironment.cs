using System;
using LunaCore;

namespace LunaMCP.Server;

/// <summary>
/// Alles, was der Server von außen braucht. <c>luna-mcp</c> setzt echtes Embedding-Modell und Windows-Dialog ein;
/// Tests und der Selbsttest ersetzen jede Abhängigkeit.
/// </summary>
public sealed record ServerEnvironment
{
    /// <summary>Aktuelle Einstellungen der App; wird bei jeder Anfrage neu gelesen (Schalter wirken sofort).</summary>
    public required Func<SharedSettings> Settings { get; init; }
    /// <summary>Überschreibt den Meeting-Ordner aus den Einstellungen (<c>--library</c>, <c>LUNA_MEETING_FOLDER</c>).</summary>
    public string? LibraryOverride { get; init; }
    public string UnsavedFolder { get; init; } = AppPaths.UnsavedMeetings;
    public string QueueFolder { get; init; } = AppPaths.MeetingQueue;
    public string IndexPath { get; init; } = AppPaths.SearchIndex;
    public ActivityLog ActivityLog { get; init; } = new();
    /// <summary>Embedding-Modell für die inhaltliche Suche; null, wenn es (noch) nicht lokal liegt.</summary>
    public Func<ITextEmbedder?> Embedder { get; init; } = () => null;
    /// <summary>Zustand des Modells für Meldungen („liegt lokal“, „nicht geladen“).</summary>
    public Func<string> ModelStatus { get; init; } = () => "Modell nicht geladen";
    public IUserConfirmer Confirmer { get; init; } = new UnavailableConfirmer();
    public IRecycleBin RecycleBin { get; init; } = WindowsRecycleBin.Instance;
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    /// <summary>Version der App.</summary>
    public string Version { get; init; } = "0";
    /// <summary>Wie oft auf Änderungen (Schalter, Bibliothek) geprüft wird, um <c>list_changed</c> zu melden; null = nie.</summary>
    public TimeSpan? ChangePollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public string LibraryFolder(SharedSettings settings) => LibraryOverride ?? settings.MeetingFolder;

    /// <summary>IANA-Name der Zeitzone („Europe/Berlin“), sonst die Windows-Kennung.</summary>
    public string TimeZoneName =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZone.Id, out var iana) ? iana : TimeZone.Id;
}
