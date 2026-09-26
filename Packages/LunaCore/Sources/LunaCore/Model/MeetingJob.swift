import Foundation

/// Ein beendetes Meeting, das auf seine Verarbeitung wartet. Liegt als `job.json` neben der Aufnahme
/// in `AppPaths.meetingQueue/<id>/` und übersteht damit einen Neustart der App. luna-mcp liest die
/// Aufträge nur, um den Verarbeitungsstand zu melden.
public struct MeetingJob: Codable, Identifiable, Equatable, Sendable {
    public var id: UUID
    public let recordedAt: Date
    public let durationSeconds: TimeInterval
    /// Beim Beenden festgelegt, damit spätere Einstellungsänderungen den Auftrag nicht verändern.
    public let language: AppLanguage
    public let expectedSpeakers: Int?
    /// Fehler des letzten Versuchs; solche Aufträge warten auf „Erneut versuchen“ oder „Verwerfen“.
    public var failure: String?

    public init(id: UUID = UUID(), recordedAt: Date, durationSeconds: TimeInterval, language: AppLanguage,
                expectedSpeakers: Int?, failure: String? = nil) {
        self.id = id
        self.recordedAt = recordedAt
        self.durationSeconds = durationSeconds
        self.language = language
        self.expectedSpeakers = expectedSpeakers
        self.failure = failure
    }

    public var title: String { MeetingTranscript.defaultTitle(for: recordedAt) }

    public static let jobFileName = "job.json"
    public static let audioFileName = "audio.wav"

    /// Liest alle Aufträge aus dem Warteschlangen-Ordner, älteste zuerst. Unlesbare werden übersprungen.
    public static func readAll(in queueFolder: URL) -> [MeetingJob] {
        let fm = FileManager.default
        guard let folders = try? fm.contentsOfDirectory(at: queueFolder, includingPropertiesForKeys: nil) else { return [] }
        return folders.compactMap { folder in
            guard let data = try? Data(contentsOf: folder.appending(path: jobFileName)),
                  fm.fileExists(atPath: folder.appending(path: audioFileName).path) else { return nil }
            return try? JSONCoding.decoder().decode(MeetingJob.self, from: data)
        }
        .sorted { $0.recordedAt < $1.recordedAt }
    }
}
