import Foundation
@testable import LunaCore

/// Hilfen für Tests: temporäre Ordner und Beispiel-Meetings.
enum Support {
    static func temporaryFolder(_ name: String = #function) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "LunaCoreTests-\(UUID().uuidString.prefix(8))", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    static func meeting(title: String = "Planung Q4", date: Date = Date(timeIntervalSince1970: 1_790_000_000),
                        lines: [(String, String)], summary: String? = nil, confidential: Bool = false) -> MeetingTranscript {
        let turns = lines.enumerated().map { index, line in
            MeetingTranscript.Turn(speakerID: line.0, start: TimeInterval(index * 10), end: TimeInterval(index * 10 + 8), text: line.1)
        }
        var meeting = MeetingTranscript(title: title, date: date, durationSeconds: TimeInterval(lines.count * 10),
                                        engine: .parakeet, language: .german, turns: turns)
        meeting.summary = summary.map { MeetingSummary(markdown: $0, createdAt: date) }
        meeting.isConfidential = confidential ? true : nil
        return meeting
    }

    static func fixtureData(_ name: String) throws -> Data {
        let url = URL(filePath: #filePath).deletingLastPathComponent().appending(path: "Fixtures/\(name)")
        return try Data(contentsOf: url)
    }
}

/// Deterministischer Ersatz für das Embedding-Modell: Bag-of-Words über gefaltete Wortstämme (5 Zeichen),
/// verteilt auf 64 Dimensionen. Genug, um Fusion und Vektorsuche zu prüfen, ohne ein Modell zu laden.
struct FakeEmbedder: TextEmbedder {
    var modelID = "fake-bow-64"
    /// Synonyme, damit „inhaltliche“ Treffer ohne gemeinsames Wort prüfbar sind.
    var synonyms: [String: String] = ["teurer": "preis", "kosten": "preis"]

    func embedDocuments(_ texts: [String]) async throws -> [[Float]] { texts.map(vector) }
    func embedQuery(_ text: String) async throws -> [Float] { vector(text) }

    private func vector(_ text: String) -> [Float] {
        var v = [Float](repeating: 0, count: 64)
        for word in TextFolding.fold(text).split(whereSeparator: { !$0.isLetter }) {
            let stem = synonyms[String(word)] ?? String(word.prefix(5))
            var hash: UInt64 = 1469598103934665603
            for byte in stem.utf8 { hash = (hash ^ UInt64(byte)) &* 1099511628211 }
            v[Int(hash % 64)] += 1
        }
        return v
    }
}
