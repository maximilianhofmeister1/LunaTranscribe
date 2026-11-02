import Foundation

/// Ein Meeting in der Warteschlange. Liegt als `job.json` neben der Aufnahme in
/// `AppPaths.meetingQueue/<id>/` und übersteht damit einen Neustart der App – und zwar von der ersten
/// Aufnahmesekunde an (`phase == .recording`), damit auch ein Absturz oder Herunterfahren mitten im
/// Meeting nichts verliert. luna-mcp liest die Aufträge nur, um den Verarbeitungsstand zu melden.
public struct MeetingJob: Codable, Identifiable, Equatable, Sendable {
    /// Wo ein Auftrag vor der Verarbeitung steht. `nil` (Feld fehlt) = beendet, wartet auf Verarbeitung –
    /// so sehen auch alle `job.json` aus, die vor Einführung des Felds geschrieben wurden.
    public enum Phase: String, Codable, Sendable {
        /// Die Aufnahme läuft noch, die WAV wächst. Läuft sie wirklich, hält der aufnehmende Prozess
        /// die `RecordingLock` im Auftragsordner.
        case recording
        /// Die Aufnahme wurde nicht regulär beendet (Absturz, Herunterfahren) und beim nächsten Start
        /// gerettet. Wird erst verarbeitet, wenn der Nutzer „Transkription starten“ wählt.
        case interrupted
    }

    public var id: UUID
    public let recordedAt: Date
    public var durationSeconds: TimeInterval
    /// Beim Beenden festgelegt, damit spätere Einstellungsänderungen den Auftrag nicht verändern
    /// (gerettete Aufnahmen behalten die Werte vom Start).
    public var language: AppLanguage
    public var expectedSpeakers: Int?
    /// Fehler des letzten Versuchs; solche Aufträge warten auf „Erneut versuchen“ oder „Verwerfen“.
    public var failure: String?
    public var phase: Phase?

    public init(id: UUID = UUID(), recordedAt: Date, durationSeconds: TimeInterval, language: AppLanguage,
                expectedSpeakers: Int?, failure: String? = nil, phase: Phase? = nil) {
        self.id = id
        self.recordedAt = recordedAt
        self.durationSeconds = durationSeconds
        self.language = language
        self.expectedSpeakers = expectedSpeakers
        self.failure = failure
        self.phase = phase
    }

    public var title: String { MeetingTranscript.defaultTitle(for: recordedAt) }

    public static let jobFileName = "job.json"
    public static let audioFileName = "audio.wav"
    /// Sperrdatei einer laufenden Aufnahme (siehe `RecordingLock`).
    public static let lockFileName = "recording.lock"

    /// Ordner eines Auftrags in der Warteschlange.
    public static func folder(for id: UUID, in queueFolder: URL = AppPaths.meetingQueue) -> URL {
        queueFolder.appending(path: id.uuidString, directoryHint: .isDirectory)
    }

    /// Liest `job.json` aus einem Auftragsordner.
    public static func read(folder: URL) throws -> MeetingJob {
        try JSONCoding.decoder().decode(MeetingJob.self, from: Data(contentsOf: folder.appending(path: jobFileName)))
    }

    /// Schreibt `job.json` atomar in den Auftragsordner (der Ordner muss existieren).
    public func write(in queueFolder: URL = AppPaths.meetingQueue) throws {
        let url = Self.folder(for: id, in: queueFolder).appending(path: Self.jobFileName)
        try JSONCoding.encoder().encode(self).write(to: url, options: .atomic)
    }

    /// Läuft die Aufnahme dieses Auftrags gerade wirklich (Sperre gehalten)?
    public func isRecordingActive(in queueFolder: URL = AppPaths.meetingQueue) -> Bool {
        phase == .recording && RecordingLock.isHeld(at: Self.folder(for: id, in: queueFolder).appending(path: Self.lockFileName))
    }

    /// Liest alle Aufträge aus dem Warteschlangen-Ordner, älteste zuerst. Unlesbare werden übersprungen.
    public static func readAll(in queueFolder: URL) -> [MeetingJob] {
        let fm = FileManager.default
        guard let folders = try? fm.contentsOfDirectory(at: queueFolder, includingPropertiesForKeys: nil) else { return [] }
        return folders.compactMap { folder in
            guard fm.fileExists(atPath: folder.appending(path: audioFileName).path) else { return nil }
            return try? read(folder: folder)
        }
        .sorted { $0.recordedAt < $1.recordedAt }
    }
}
