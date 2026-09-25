import Foundation

/// Alle Ablageorte der App. Nutzerdaten (Meetings) liegen in einem frei wählbaren Ordner;
/// alles andere unter den üblichen Library-Ordnern.
enum AppPaths {
    /// ~/Library/Application Support/LunaTranscribe
    static var applicationSupport: URL {
        URL.applicationSupportDirectory.appending(path: "LunaTranscribe", directoryHint: .isDirectory)
    }

    /// Alle Modelle (ASR, Sprecher, Sprachmodelle). Das CTC-Boosting-Modell liegt abweichend in
    /// FluidAudios eigenem Cache, weil dessen Tokenizer-Suche dort fest verdrahtet ist.
    static var models: URL { applicationSupport.appending(path: "Models", directoryHint: .isDirectory) }

    /// Rettungsordner für Meetings, wenn der eingestellte Meeting-Ordner nicht erreichbar ist.
    static var unsavedMeetings: URL { applicationSupport.appending(path: "Unsaved", directoryHint: .isDirectory) }

    /// Beendete, noch nicht verarbeitete Meetings (Aufnahme + Auftrag). Bewusst nicht im Cache:
    /// Die Warteschlange muss einen Neustart überstehen.
    static var meetingQueue: URL { applicationSupport.appending(path: "Queue", directoryHint: .isDirectory) }

    /// Laufende Meeting-Aufnahmen; wandern beim Beenden in die Warteschlange.
    static var meetingRecordings: URL {
        URL.cachesDirectory.appending(path: "LunaTranscribe/Meetings", directoryHint: .isDirectory)
    }

    /// Standard-Meeting-Ordner, falls in den Einstellungen keiner gesetzt ist.
    static var defaultMeetingFolder: URL {
        URL.documentsDirectory.appending(path: "LunaTranscribe Meetings", directoryHint: .isDirectory)
    }

    /// Meeting-Ordner aus der Einstellung ("" = Standard, `~` wird aufgelöst).
    static func meetingFolder(from settingsPath: String) -> URL {
        guard !settingsPath.isEmpty else { return defaultMeetingFolder }
        return URL(filePath: (settingsPath as NSString).expandingTildeInPath, directoryHint: .isDirectory)
    }
}
