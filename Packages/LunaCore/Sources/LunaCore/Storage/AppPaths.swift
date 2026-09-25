import Foundation

/// Alle Ablageorte der App. Nutzerdaten (Meetings) liegen in einem frei wählbaren Ordner;
/// alles andere unter den üblichen Library-Ordnern. App und luna-mcp nutzen dieselben Pfade.
public enum AppPaths {
    /// ~/Library/Application Support/LunaTranscribe
    public static var applicationSupport: URL {
        directory(.applicationSupportDirectory).appending(path: "LunaTranscribe", directoryHint: .isDirectory)
    }

    /// Alle Modelle (ASR, Sprecher, Sprachmodelle, Embeddings). Das CTC-Boosting-Modell liegt abweichend
    /// in FluidAudios eigenem Cache, weil dessen Tokenizer-Suche dort fest verdrahtet ist.
    public static var models: URL { applicationSupport.appending(path: "Models", directoryHint: .isDirectory) }

    /// Rettungsordner für Meetings, wenn der eingestellte Meeting-Ordner nicht erreichbar ist.
    public static var unsavedMeetings: URL { applicationSupport.appending(path: "Unsaved", directoryHint: .isDirectory) }

    /// Beendete, noch nicht verarbeitete Meetings (Aufnahme + Auftrag). Bewusst nicht im Cache:
    /// Die Warteschlange muss einen Neustart überstehen.
    public static var meetingQueue: URL { applicationSupport.appending(path: "Queue", directoryHint: .isDirectory) }

    /// Suchindex (SQLite). Abgeleitete Daten: jederzeit löschbar, wird aus der Bibliothek neu aufgebaut.
    /// Bewusst nicht im Meeting-Ordner, der soll im Finder übersichtlich bleiben.
    public static var searchIndex: URL {
        applicationSupport.appending(path: "Index", directoryHint: .isDirectory).appending(path: "search.sqlite")
    }

    /// Was Claude über luna-mcp gelesen und geändert hat (JSON Lines, nur IDs und Metadaten).
    public static var mcpActivityLog: URL { applicationSupport.appending(path: "mcp-activity.log") }

    /// Laufende Meeting-Aufnahmen; wandern beim Beenden in die Warteschlange.
    public static var meetingRecordings: URL {
        directory(.cachesDirectory).appending(path: "LunaTranscribe/Meetings", directoryHint: .isDirectory)
    }

    /// Standard-Meeting-Ordner, falls in den Einstellungen keiner gesetzt ist.
    public static var defaultMeetingFolder: URL {
        directory(.documentDirectory).appending(path: "LunaTranscribe Meetings", directoryHint: .isDirectory)
    }

    /// Wie `URL.applicationSupportDirectory` & Co., aber auch unter Linux verfügbar (Tests).
    private static func directory(_ kind: FileManager.SearchPathDirectory) -> URL {
        FileManager.default.urls(for: kind, in: .userDomainMask).first
            ?? URL.homeDirectory.appending(path: "Library", directoryHint: .isDirectory)
    }

    /// Meeting-Ordner aus der Einstellung ("" = Standard, `~` wird aufgelöst).
    public static func meetingFolder(from settingsPath: String) -> URL {
        guard !settingsPath.isEmpty else { return defaultMeetingFolder }
        return URL(filePath: (settingsPath as NSString).expandingTildeInPath, directoryHint: .isDirectory)
    }
}
