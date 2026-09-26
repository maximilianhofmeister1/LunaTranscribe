import Foundation

/// Ein Eintrag im Aktivitätsprotokoll von luna-mcp. Enthält nie Transkript-Inhalte – nur IDs und bei
/// Umbenennungen den alten und neuen Namen, damit die App sie rückgängig machen kann.
public struct ActivityEntry: Codable, Equatable, Sendable {
    public enum Action: String, Codable, Sendable {
        case read
        case search
        case renameMeeting
        case renameSpeaker
        case delete
        case deleteDeclined
    }

    public var date: Date
    /// Name des Claude-Clients („claude-ai“, „claude-code“ …), wie er sich beim Verbinden meldet.
    public var client: String
    public var action: Action
    public var meetingIDs: [UUID]
    /// Sprecher-ID bei `renameSpeaker`.
    public var speakerID: String?
    public var oldValue: String?
    public var newValue: String?

    public init(date: Date = Date(), client: String, action: Action, meetingIDs: [UUID],
                speakerID: String? = nil, oldValue: String? = nil, newValue: String? = nil) {
        self.date = date
        self.client = client
        self.action = action
        self.meetingIDs = meetingIDs
        self.speakerID = speakerID
        self.oldValue = oldValue
        self.newValue = newValue
    }

    /// Hat dieser Eintrag die Bibliothek verändert? Die App lädt dann neu.
    public var isChange: Bool {
        switch action {
        case .renameMeeting, .renameSpeaker, .delete: return true
        case .read, .search, .deleteDeclined: return false
        }
    }
}

/// JSON-Lines-Datei unter Application Support. luna-mcp hängt an, die App liest (Anzeige, Rückgängig)
/// und beobachtet die Datei, um Änderungen von außen sofort zu übernehmen.
public struct ActivityLog: Sendable {
    public let url: URL
    /// Ältere Einträge fallen beim Aufräumen weg.
    public var retention: TimeInterval = 30 * 24 * 3600
    /// Ab dieser Größe wird beim nächsten Anhängen aufgeräumt.
    public var compactionThreshold = 512 * 1024

    public init(url: URL = AppPaths.mcpActivityLog) {
        self.url = url
    }

    public func append(_ entry: ActivityEntry) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var line = try JSONCoding.encoder(pretty: false).encode(entry)
        line.append(0x0A)
        if !fm.fileExists(atPath: url.path) {
            try line.write(to: url, options: .atomic)
            return
        }
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        let size = try handle.seekToEnd()
        try handle.write(contentsOf: line)
        if size > UInt64(compactionThreshold) { try compact() }
    }

    /// Einträge, neueste zuerst. Beschädigte Zeilen werden übersprungen.
    public func entries(limit: Int? = nil) -> [ActivityEntry] {
        guard let data = try? Data(contentsOf: url) else { return [] }
        let decoder = JSONCoding.decoder()
        let all = data.split(separator: 0x0A).compactMap { try? decoder.decode(ActivityEntry.self, from: Data($0)) }
        let newestFirst = all.reversed()
        return limit.map { Array(newestFirst.prefix($0)) } ?? Array(newestFirst)
    }

    /// Wirft Einträge älter als `retention` weg.
    public func compact(now: Date = Date()) throws {
        let keep = entries().reversed().filter { now.timeIntervalSince($0.date) <= retention }
        let encoder = JSONCoding.encoder(pretty: false)
        var data = Data()
        for entry in keep {
            data.append(try encoder.encode(entry))
            data.append(0x0A)
        }
        try data.write(to: url, options: .atomic)
    }
}
