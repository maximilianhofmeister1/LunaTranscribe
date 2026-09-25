import Foundation
import LunaCore

/// Alles, was der Server von außen braucht. Die macOS-Variante (`luna-mcp`) setzt echtes Embedding-Modell
/// und AppKit-Dialog ein; Tests ersetzen jede Abhängigkeit.
public struct ServerEnvironment: Sendable {
    /// Aktuelle Einstellungen der App; wird bei jeder Anfrage neu gelesen (Schalter wirken sofort).
    public var settings: @Sendable () -> SharedSettings
    /// Überschreibt den Meeting-Ordner aus den Einstellungen (`--library`, `LUNA_MEETING_FOLDER`).
    public var libraryOverride: URL?
    public var unsavedFolder: URL
    public var queueFolder: URL
    public var indexURL: URL
    public var activityLog: ActivityLog
    /// Embedding-Modell für die inhaltliche Suche; nil, wenn es (noch) nicht lokal liegt.
    public var embedder: @Sendable () async -> (any TextEmbedder)?
    public var confirmer: any UserConfirmer
    public var now: @Sendable () -> Date
    public var timeZone: TimeZone
    /// Version der App (`CFBundleShortVersionString`).
    public var version: String
    /// Wie oft auf Änderungen (Schalter, Bibliothek) geprüft wird, um `list_changed` zu melden; nil = nie.
    public var changePollInterval: Duration?

    public init(settings: @escaping @Sendable () -> SharedSettings,
                libraryOverride: URL? = nil,
                unsavedFolder: URL = AppPaths.unsavedMeetings,
                queueFolder: URL = AppPaths.meetingQueue,
                indexURL: URL = AppPaths.searchIndex,
                activityLog: ActivityLog = ActivityLog(),
                embedder: @escaping @Sendable () async -> (any TextEmbedder)? = { nil },
                confirmer: any UserConfirmer = UnavailableConfirmer(),
                now: @escaping @Sendable () -> Date = { Date() },
                timeZone: TimeZone = .current,
                version: String = "0",
                changePollInterval: Duration? = .seconds(3)) {
        self.settings = settings
        self.libraryOverride = libraryOverride
        self.unsavedFolder = unsavedFolder
        self.queueFolder = queueFolder
        self.indexURL = indexURL
        self.activityLog = activityLog
        self.embedder = embedder
        self.confirmer = confirmer
        self.now = now
        self.timeZone = timeZone
        self.version = version
        self.changePollInterval = changePollInterval
    }

    func libraryFolder(_ settings: SharedSettings) -> URL {
        libraryOverride ?? settings.meetingFolder
    }
}
