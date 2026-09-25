import Foundation
import LunaCore

/// Hybride Suche für den Server: hält den Suchindex auf dem Stand des Katalogs, berechnet fehlende
/// Vektoren im Hintergrund und führt Stichwort-, Vektor- und Titel-Treffer zusammen.
actor SearchService {
    enum Mode: String, CaseIterable, Sendable {
        case auto
        case keyword
        case semantic
    }

    enum SemanticState: Equatable, Sendable {
        case used(coverage: Double)
        case disabled
        case modelMissing
        case notRequested
        case failed(String)

        var note: String? {
            switch self {
            case .used(let coverage) where coverage < 0.999:
                return "Die inhaltliche Suche indexiert noch (\(Int(coverage * 100)) % fertig) – ältere Meetings fehlen dort eventuell."
            case .used, .notRequested: return nil
            case .disabled:
                return "Inhaltliche Suche ist aus (LunaTranscribe → Einstellungen → Meetings). Es wurde nur nach Stichworten gesucht."
            case .modelMissing:
                return "Das Modell für die inhaltliche Suche ist noch nicht geladen (LunaTranscribe → Einstellungen → Meetings). Es wurde nur nach Stichworten gesucht."
            case .failed(let message):
                return "Inhaltliche Suche fehlgeschlagen (\(message)). Es wurde nur nach Stichworten gesucht."
            }
        }
    }

    struct Filter: Sendable {
        var from: Date?
        var to: Date?
        /// Gefalteter Sprechername oder -ID.
        var speaker: String?
    }

    struct Result: Sendable {
        let hits: [SearchHit]
        let semantic: SemanticState
    }

    private let indexURL: URL
    private let embedderProvider: @Sendable () async -> (any TextEmbedder)?
    private var index: SearchIndex?
    private var syncedGeneration = -1
    private var embedTask: Task<Void, Never>?

    init(indexURL: URL, embedder: @escaping @Sendable () async -> (any TextEmbedder)?) {
        self.indexURL = indexURL
        self.embedderProvider = embedder
    }

    func search(_ text: String, mode: Mode, filter: Filter, limit: Int, snapshot: MeetingCatalog.Snapshot,
                settings: SharedSettings) async throws -> Result {
        let index = try await synced(snapshot)
        let meetings = Dictionary(snapshot.meetings.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let query = KeywordQuery(text, vocabulary: settings.vocabularyEntries)
        let candidates = max(limit * 6, 50)

        func allowed(_ record: SearchIndex.ChunkRecord) -> Bool {
            guard let meeting = meetings[record.meetingID], Self.matches(meeting, filter) else { return false }
            guard let speaker = filter.speaker else { return true }
            let ids = Self.speakerIDs(matching: speaker, in: meeting.transcript)
            return !ids.isDisjoint(with: record.speakerIDs)
        }

        var keyword: [SearchIndex.KeywordHit] = []
        if mode != .semantic {
            keyword = try await index.keywordSearch(query, limit: candidates).filter { allowed($0.record) }
        }

        var semantic: [SearchIndex.VectorHit] = []
        var state = SemanticState.notRequested
        if mode != .keyword {
            (semantic, state) = await semanticHits(text, index: index, settings: settings, limit: candidates)
            semantic = semantic.filter { allowed($0.record) }
        }

        let metadata = mode == .semantic ? [] : Self.metadataMatches(query.terms, in: snapshot.meetings, filter: filter)
        let hits = HybridRanker.fuse(keyword: keyword, semantic: semantic, metadata: metadata, limit: limit)
        return Result(hits: hits, semantic: state)
    }

    func stats(snapshot: MeetingCatalog.Snapshot, settings: SharedSettings) async -> String {
        do {
            let index = try await synced(snapshot)
            let embedder = settings.semanticSearchEnabled ? await embedderProvider() : nil
            let stats = try await index.stats(model: embedder?.modelID)
            var line = "Suchindex: \(stats.meetings) Meetings, \(stats.chunks) Abschnitte."
            if let embedder {
                line += " Inhaltliche Suche (\(embedder.modelID)): \(stats.embedded) von \(stats.chunks) Abschnitten berechnet."
            } else if settings.semanticSearchEnabled {
                line += " Inhaltliche Suche: Modell noch nicht geladen."
            } else {
                line += " Inhaltliche Suche: aus."
            }
            return line
        } catch {
            return "Suchindex nicht verfügbar: \(error.localizedDescription)"
        }
    }

    // MARK: Intern

    private func synced(_ snapshot: MeetingCatalog.Snapshot) async throws -> SearchIndex {
        let index = try self.index ?? SearchIndex(url: indexURL)
        self.index = index
        if snapshot.generation != syncedGeneration {
            try await index.sync(snapshot.meetings)
            syncedGeneration = snapshot.generation
        }
        return index
    }

    private func semanticHits(_ text: String, index: SearchIndex, settings: SharedSettings,
                              limit: Int) async -> ([SearchIndex.VectorHit], SemanticState) {
        guard settings.semanticSearchEnabled else { return ([], .disabled) }
        guard let embedder = await embedderProvider() else { return ([], .modelMissing) }
        startEmbedding(index: index, embedder: embedder)
        do {
            let vector = try await embedder.embedQuery(text)
            let hits = try await index.vectorSearch(vector, model: embedder.modelID, limit: limit)
            let stats = try await index.stats(model: embedder.modelID)
            let coverage = stats.chunks == 0 ? 1 : Double(stats.embedded) / Double(stats.chunks)
            return (hits, .used(coverage: coverage))
        } catch {
            return ([], .failed(error.localizedDescription))
        }
    }

    /// Fehlende Vektoren im Hintergrund nachrechnen; die Suche wartet nicht darauf.
    private func startEmbedding(index: SearchIndex, embedder: any TextEmbedder) {
        guard embedTask == nil else { return }
        embedTask = Task { [weak self] in
            _ = try? await index.embedPending(with: embedder)
            await self?.embeddingFinished()
        }
    }

    private func embeddingFinished() {
        embedTask = nil
    }

    /// Wartet auf eine laufende Hintergrund-Berechnung (für Tests und `--selftest`).
    func waitForEmbedding() async {
        await embedTask?.value
    }

    static func matches(_ meeting: StoredMeeting, _ filter: Filter) -> Bool {
        if let from = filter.from, meeting.transcript.date < from { return false }
        if let to = filter.to, meeting.transcript.date >= to { return false }
        if let speaker = filter.speaker, speakerIDs(matching: speaker, in: meeting.transcript).isEmpty { return false }
        return true
    }

    /// Sprecher-IDs, deren ID oder Anzeigename den (gefalteten) Suchtext enthält.
    static func speakerIDs(matching folded: String, in transcript: MeetingTranscript) -> Set<String> {
        Set(transcript.speakerIDs.filter {
            TextFolding.fold($0).contains(folded) || TextFolding.fold(transcript.displayName(for: $0)).contains(folded)
        })
    }

    /// Meetings, deren Titel oder Sprechernamen mindestens die Hälfte der Suchwörter enthalten.
    static func metadataMatches(_ terms: [String], in meetings: [StoredMeeting], filter: Filter) -> [UUID] {
        guard !terms.isEmpty else { return [] }
        let needed = (terms.count + 1) / 2
        return meetings.compactMap { meeting -> (UUID, Int, Date)? in
            guard matches(meeting, filter) else { return nil }
            let t = meeting.transcript
            let haystack = TextFolding.fold(([t.title] + t.speakerIDs.map(t.displayName(for:))).joined(separator: " "))
            let found = terms.filter { haystack.contains($0) }.count
            return found >= needed ? (meeting.id, found, t.date) : nil
        }
        .sorted { $0.1 != $1.1 ? $0.1 > $1.1 : $0.2 > $1.2 }
        .map(\.0)
    }
}
