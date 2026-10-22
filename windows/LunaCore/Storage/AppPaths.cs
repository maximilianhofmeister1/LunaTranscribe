using System;
using System.IO;

namespace LunaCore;

/// <summary>
/// Alle Ablageorte der App. Nutzerdaten (Meetings) liegen in einem frei wählbaren Ordner; alles andere unter
/// <c>%LOCALAPPDATA%\LunaTranscribe</c>. App und luna-mcp nutzen dieselben Pfade.
/// Für Tests lässt sich die Basis über <see cref="DataDirectoryOverride"/> oder die Umgebungsvariable
/// <c>LUNA_DATA_DIR</c> umlenken.
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "LUNA_DATA_DIR";

    /// <summary>Ersetzt die Basis (Vorrang vor <c>LUNA_DATA_DIR</c>); null = Standard.</summary>
    public static string? DataDirectoryOverride { get; set; }

    /// <summary>%LOCALAPPDATA%\LunaTranscribe (Gegenstück zu ~/Library/Application Support/LunaTranscribe).</summary>
    public static string DataDirectory
    {
        get
        {
            if (!string.IsNullOrEmpty(DataDirectoryOverride)) return DataDirectoryOverride;
            var env = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaTranscribe");
        }
    }

    /// <summary>Alle Modelle (ASR, Sprecher, Sprachmodelle, Embeddings).</summary>
    public static string Models => Path.Combine(DataDirectory, "Models");

    /// <summary>Rettungsordner für Meetings, wenn der eingestellte Meeting-Ordner nicht erreichbar ist.</summary>
    public static string UnsavedMeetings => Path.Combine(DataDirectory, "Unsaved");

    /// <summary>Beendete, noch nicht verarbeitete Meetings (Aufnahme + Auftrag). Muss einen Neustart überstehen.</summary>
    public static string MeetingQueue => Path.Combine(DataDirectory, "Queue");

    /// <summary>
    /// Suchindex (SQLite). Abgeleitete Daten: jederzeit löschbar, wird aus der Bibliothek neu aufgebaut.
    /// Bewusst nicht im Meeting-Ordner, der soll im Explorer übersichtlich bleiben.
    /// </summary>
    public static string SearchIndex => Path.Combine(DataDirectory, "Index", "search.sqlite");

    /// <summary>Was Claude über luna-mcp gelesen und geändert hat (JSON Lines, nur IDs und Metadaten).</summary>
    public static string McpActivityLog => Path.Combine(DataDirectory, "mcp-activity.log");

    /// <summary>
    /// Laufende Meeting-Aufnahmen; wandern beim Beenden in die Warteschlange. Windows hat keinen Caches-Ordner,
    /// deshalb unter der Basis statt unter ~/Library/Caches.
    /// </summary>
    public static string MeetingRecordings => Path.Combine(DataDirectory, "Recordings");

    /// <summary>Standard-Meeting-Ordner (Dokumente\LunaTranscribe Meetings), falls in den Einstellungen keiner gesetzt ist.</summary>
    public static string DefaultMeetingFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LunaTranscribe Meetings");

    /// <summary>Meeting-Ordner aus der Einstellung ("" = Standard; <c>%VARS%</c> und ein führendes <c>~</c> werden aufgelöst).</summary>
    public static string MeetingFolder(string? settingsPath)
    {
        if (string.IsNullOrEmpty(settingsPath)) return DefaultMeetingFolder;
        var path = Environment.ExpandEnvironmentVariables(settingsPath);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }
}
