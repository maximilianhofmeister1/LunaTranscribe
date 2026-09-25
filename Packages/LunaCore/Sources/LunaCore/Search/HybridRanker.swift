import Foundation

/// Ein Treffer der kombinierten Suche.
public struct SearchHit: Sendable {
    public enum Source: String, Sendable, Comparable {
        case title = "Titel/Sprecher"
        case keyword = "Stichwort"
        case semantic = "inhaltlich"

        public static func < (lhs: Source, rhs: Source) -> Bool { lhs.rawValue < rhs.rawValue }
    }

    public let meetingID: UUID
    /// Fundstelle im Meeting; nil, wenn nur Titel oder Sprechername passen.
    public let chunk: SearchIndex.ChunkRecord?
    /// Ausschnitt aus der Stichwortsuche (mit **Markierung**), falls vorhanden.
    public var snippet: String?
    public var sources: Set<Source>
    public var score: Double
}

/// Führt Stichwort-, Vektor- und Titel-Treffer per **Reciprocal Rank Fusion** zusammen:
/// Score = Σ 1 / (k + Rang). RRF braucht keine Kalibrierung der unvergleichbaren Einzelscores
/// (BM25 vs. Kosinus) und ist deshalb das übliche Verfahren für hybride Suche.
public enum HybridRanker {
    /// Üblicher Wert aus der Literatur (Cormack et al., 2009); dämpft den Vorsprung der Spitzenplätze.
    public static let k = 60.0

    /// - Parameters:
    ///   - metadata: Meetings, deren Titel oder Sprechernamen passen, beste zuerst.
    ///   - perMeeting: höchstens so viele Treffer je Meeting, damit ein langes Meeting nicht alles belegt.
    public static func fuse(keyword: [SearchIndex.KeywordHit], semantic: [SearchIndex.VectorHit], metadata: [UUID],
                            perMeeting: Int = 3, limit: Int) -> [SearchHit] {
        var hits: [String: SearchHit] = [:]

        func add(key: String, meetingID: UUID, chunk: SearchIndex.ChunkRecord?, snippet: String?,
                 source: SearchHit.Source, rank: Int) {
            let contribution = 1 / (k + Double(rank + 1))
            if var hit = hits[key] {
                hit.score += contribution
                hit.sources.insert(source)
                if hit.snippet == nil { hit.snippet = snippet }
                hits[key] = hit
            } else {
                hits[key] = SearchHit(meetingID: meetingID, chunk: chunk, snippet: snippet, sources: [source], score: contribution)
            }
        }

        for (rank, hit) in keyword.enumerated() {
            add(key: "c\(hit.record.id)", meetingID: hit.record.meetingID, chunk: hit.record, snippet: hit.snippet,
                source: .keyword, rank: rank)
        }
        for (rank, hit) in semantic.enumerated() {
            add(key: "c\(hit.record.id)", meetingID: hit.record.meetingID, chunk: hit.record, snippet: nil,
                source: .semantic, rank: rank)
        }
        for (rank, id) in metadata.enumerated() {
            add(key: "m\(id.uuidString)", meetingID: id, chunk: nil, snippet: nil, source: .title, rank: rank)
        }

        var perMeetingCount: [UUID: Int] = [:]
        return hits.values
            .sorted { $0.score != $1.score ? $0.score > $1.score : ($0.chunk?.id ?? 0) < ($1.chunk?.id ?? 0) }
            .filter { hit in
                let count = perMeetingCount[hit.meetingID, default: 0]
                guard count < perMeeting else { return false }
                perMeetingCount[hit.meetingID] = count + 1
                return true
            }
            .prefix(limit)
            .map { $0 }
    }
}
