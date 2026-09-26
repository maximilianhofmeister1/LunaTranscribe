import Foundation

/// Suchindex über die Meeting-Bibliothek: Stichwortsuche (SQLite FTS5, BM25) und Vektoren für die
/// inhaltliche Suche in einer Datei.
///
/// - **Abgeleitete Daten:** Der Index lässt sich jederzeit löschen; `sync` baut ihn aus der Bibliothek neu.
/// - **Mehrere Prozesse:** App und luna-mcp öffnen dieselbe Datei. WAL-Modus und `busy_timeout` machen das
///   sicher; `sync` ist idempotent (Änderungsmerkmal je Meeting), doppelte Arbeit ist harmlos.
/// - **Keine Namen im Index:** Titel und Sprechernamen löst der Aufrufer live aus `meeting.json` auf – so
///   macht Umbenennen den Index nie ungültig.
/// - **Vertrauliche Meetings** werden nie aufgenommen und beim nächsten `sync` entfernt.
public actor SearchIndex {
    public struct ChunkRecord: Equatable, Sendable {
        public let id: Int64
        public let meetingID: UUID
        public let kind: Chunk.Kind
        public let turns: ClosedRange<Int>?
        public let start: TimeInterval
        public let end: TimeInterval
        public let speakerIDs: [String]
        public let text: String
    }

    public struct KeywordHit: Sendable {
        public let record: ChunkRecord
        /// Ausschnitt mit **markierten** Fundstellen.
        public let snippet: String
    }

    public struct VectorHit: Sendable {
        public let record: ChunkRecord
        public let similarity: Float
    }

    public struct SyncReport: Equatable, Sendable {
        public var added = 0
        public var updated = 0
        public var removed = 0
        public var unchanged = 0
        public var hasChanges: Bool { added + updated + removed > 0 }
    }

    public struct Stats: Equatable, Sendable {
        public let meetings: Int
        public let chunks: Int
        /// Abschnitte mit Vektor des angefragten Modells.
        public let embedded: Int
    }

    static let schemaVersion = 1

    private let db: SQLiteDatabase
    /// Zählt eigene Schreibvorgänge; zusammen mit `PRAGMA data_version` (fremde) entwertet er den Vektor-Cache.
    private var localWrites = 0
    private var vectorCache: VectorCache?
    private var isEmbedding = false

    private struct VectorCache {
        let model: String
        let stamp: [Int]
        let ids: [Int64]
        let dimensions: Int
        let matrix: [Float]
    }

    public init(url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let db = try SQLiteDatabase(url: url)
        try Self.prepareSchema(db)
        self.db = db
    }

    // MARK: Abgleich

    /// Bringt den Stichwort-Teil auf den Stand von `meetings` (ohne Vektoren – dafür `embedPending`).
    @discardableResult
    public func sync(_ meetings: [StoredMeeting]) throws -> SyncReport {
        let existing = Dictionary(
            try db.prepare("SELECT id, content_hash FROM meetings").query { ($0.string(0), $0.string(1)) },
            uniquingKeysWith: { first, _ in first })
        var report = SyncReport()

        try db.transaction {
            let insertMeeting = try db.prepare("INSERT INTO meetings(id, content_hash, indexed_at) VALUES (?, ?, ?)")
            let insertChunk = try db.prepare("""
                INSERT INTO chunks(meeting_id, ordinal, kind, turn_from, turn_to, start_s, end_s, speaker_ids, text)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """)
            var seen = Set<String>()
            for meeting in meetings where !meeting.transcript.confidential {
                let id = meeting.id.uuidString
                // Kopierte Meeting-Ordner tragen dieselbe ID – der erste gewinnt.
                guard seen.insert(id).inserted else { continue }
                let chunks = Chunker.chunks(for: meeting.transcript)
                let hash = Self.contentHash(chunks)
                if existing[id] == hash {
                    report.unchanged += 1
                    continue
                }
                if existing[id] != nil {
                    try deleteMeeting(id)
                    report.updated += 1
                } else {
                    report.added += 1
                }
                try insertMeeting.run([.text(id), .text(hash), .double(Date().timeIntervalSince1970)])
                for (ordinal, chunk) in chunks.enumerated() {
                    try insertChunk.run([
                        .text(id), .int(ordinal), .int(chunk.kind.rawValue),
                        chunk.turns.map { .int($0.lowerBound) } ?? .null,
                        chunk.turns.map { .int($0.upperBound) } ?? .null,
                        .double(chunk.start), .double(chunk.end),
                        .text(chunk.speakerIDs.joined(separator: "\n")), .text(chunk.text),
                    ])
                }
            }
            for id in existing.keys where !seen.contains(id) {
                try deleteMeeting(id)
                report.removed += 1
            }
        }
        if report.hasChanges { localWrites += 1 }
        return report
    }

    /// Berechnet fehlende Vektoren für `embedder.modelID` in Stapeln. Läuft höchstens einmal gleichzeitig;
    /// Vektoren anderer Modelle werden verworfen. Gibt die Zahl neu eingebetteter Abschnitte zurück.
    @discardableResult
    public func embedPending(with embedder: any TextEmbedder, batchSize: Int = 16, maxChunks: Int = .max) async throws -> Int {
        guard !isEmbedding else { return 0 }
        isEmbedding = true
        defer { isEmbedding = false }

        let model = embedder.modelID
        try db.prepare("DELETE FROM vectors WHERE model <> ?").run([.text(model)])
        let pending = try db.prepare("""
            SELECT c.id, c.text FROM chunks c LEFT JOIN vectors v ON v.chunk_id = c.id
            WHERE v.chunk_id IS NULL ORDER BY c.id LIMIT ?
            """)
        var done = 0
        while done < maxChunks {
            let batch = try pending.query([.int(min(batchSize, maxChunks - done))]) { (Int64($0.int(0)), $0.string(1)) }
            guard !batch.isEmpty else { break }
            let vectors = try await embedder.embedDocuments(batch.map(\.1))
            guard vectors.count == batch.count else {
                throw SearchIndexError.embeddingMismatch(expected: batch.count, got: vectors.count)
            }
            try db.transaction {
                // Ein Abschnitt kann während des Rechnens verschwunden sein (anderer Prozess, `sync`).
                let insert = try db.prepare("""
                    INSERT OR REPLACE INTO vectors(chunk_id, model, vector)
                    SELECT ?1, ?2, ?3 WHERE EXISTS (SELECT 1 FROM chunks WHERE id = ?1)
                    """)
                for (row, vector) in zip(batch, vectors) {
                    try insert.run([.int(Int(row.0)), .text(model), .blob(VectorMath.data(VectorMath.normalized(vector)))])
                }
            }
            localWrites += 1
            done += batch.count
            try Task.checkCancellation()
        }
        return done
    }

    // MARK: Suche

    /// Stichwortsuche: erst alle Bestandteile (UND); findet das zu wenig, ergänzt ODER-Treffer.
    public func keywordSearch(_ query: KeywordQuery, limit: Int) throws -> [KeywordHit] {
        guard let all = query.all else { return [] }
        var hits = try match(all, limit: limit)
        if hits.count < limit / 2 + 1, let any = query.any {
            let known = Set(hits.map(\.record.id))
            hits += try match(any, limit: limit).filter { !known.contains($0.record.id) }
        }
        return Array(hits.prefix(limit))
    }

    /// Nächste Nachbarn per Kosinus-Ähnlichkeit (Vektoren sind normalisiert → Skalarprodukt).
    /// Leer, wenn für `model` keine passenden Vektoren existieren.
    public func vectorSearch(_ query: [Float], model: String, limit: Int) throws -> [VectorHit] {
        let cache = try vectors(for: model)
        let q = VectorMath.normalized(query)
        guard cache.dimensions == q.count, !cache.ids.isEmpty else { return [] }

        var scored: [(similarity: Float, index: Int)] = []
        scored.reserveCapacity(cache.ids.count)
        cache.matrix.withUnsafeBufferPointer { matrix in
            q.withUnsafeBufferPointer { q in
                for row in 0 ..< cache.ids.count {
                    var sum: Float = 0
                    let offset = row * cache.dimensions
                    for d in 0 ..< cache.dimensions { sum += matrix[offset + d] * q[d] }
                    scored.append((sum, row))
                }
            }
        }
        let top = scored.sorted { $0.similarity > $1.similarity }.prefix(limit)
        let records = try self.records(top.map { cache.ids[$0.index] })
        return top.compactMap { item in
            records[cache.ids[item.index]].map { VectorHit(record: $0, similarity: item.similarity) }
        }
    }

    public func stats(model: String? = nil) throws -> Stats {
        Stats(meetings: try db.scalarInt("SELECT COUNT(*) FROM meetings"),
              chunks: try db.scalarInt("SELECT COUNT(*) FROM chunks"),
              embedded: try model.map { try db.scalarInt("SELECT COUNT(*) FROM vectors WHERE model = ?", [.text($0)]) } ?? 0)
    }

    // MARK: Intern

    private func match(_ expression: String, limit: Int) throws -> [KeywordHit] {
        let statement = try db.prepare("""
            SELECT c.id, c.meeting_id, c.kind, c.turn_from, c.turn_to, c.start_s, c.end_s, c.speaker_ids, c.text,
                   snippet(chunks_fts, 0, '**', '**', ' … ', 24)
            FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid
            WHERE chunks_fts MATCH ? ORDER BY bm25(chunks_fts) LIMIT ?
            """)
        return try statement.query([.text(expression), .int(limit)]) { row in
            guard let record = Self.record(row) else { return nil }
            return KeywordHit(record: record, snippet: row.string(9))
        }.compactMap { $0 }
    }

    private func records(_ ids: [Int64]) throws -> [Int64: ChunkRecord] {
        guard !ids.isEmpty else { return [:] }
        let placeholders = Array(repeating: "?", count: ids.count).joined(separator: ",")
        let statement = try db.prepare("""
            SELECT id, meeting_id, kind, turn_from, turn_to, start_s, end_s, speaker_ids, text
            FROM chunks WHERE id IN (\(placeholders))
            """)
        let rows = try statement.query(ids.map { .int(Int($0)) }) { Self.record($0) }
        return Dictionary(rows.compactMap { $0 }.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
    }

    private static func record(_ row: SQLiteStatement) -> ChunkRecord? {
        guard let meetingID = UUID(uuidString: row.string(1)) else { return nil }
        let turns: ClosedRange<Int>? = row.isNull(3) || row.isNull(4) ? nil : row.int(3) ... max(row.int(3), row.int(4))
        let speakers = row.string(7).split(separator: "\n").map(String.init)
        return ChunkRecord(id: Int64(row.int(0)), meetingID: meetingID, kind: Chunk.Kind(rawValue: row.int(2)) ?? .turns,
                           turns: turns, start: row.double(5), end: row.double(6), speakerIDs: speakers, text: row.string(8))
    }

    private func vectors(for model: String) throws -> VectorCache {
        let stamp = [try db.scalarInt("PRAGMA data_version"), localWrites]
        if let cache = vectorCache, cache.model == model, cache.stamp == stamp { return cache }

        var ids: [Int64] = []
        var matrix: [Float] = []
        var dimensions = 0
        let statement = try db.prepare("SELECT chunk_id, vector FROM vectors WHERE model = ? ORDER BY chunk_id")
        try statement.bind([.text(model)])
        while try statement.step() {
            let vector = VectorMath.vector(statement.blob(1))
            if dimensions == 0 { dimensions = vector.count }
            guard vector.count == dimensions, dimensions > 0 else { continue }
            ids.append(Int64(statement.int(0)))
            matrix.append(contentsOf: vector)
        }
        let cache = VectorCache(model: model, stamp: stamp, ids: ids, dimensions: dimensions, matrix: matrix)
        vectorCache = cache
        return cache
    }

    private func deleteMeeting(_ id: String) throws {
        try db.prepare("DELETE FROM vectors WHERE chunk_id IN (SELECT id FROM chunks WHERE meeting_id = ?)").run([.text(id)])
        try db.prepare("DELETE FROM chunks WHERE meeting_id = ?").run([.text(id)])
        try db.prepare("DELETE FROM meetings WHERE id = ?").run([.text(id)])
    }

    /// Änderungsmerkmal eines Meetings: Zerlegungsversion + alle Abschnitte.
    static func contentHash(_ chunks: [Chunk]) -> String {
        var text = "v\(Chunker.version)"
        for chunk in chunks {
            text += "\u{1E}\(chunk.kind.rawValue)|\(chunk.turns.map { "\($0.lowerBound)-\($0.upperBound)" } ?? "")|"
            text += "\(chunk.start)|\(chunk.end)|\(chunk.speakerIDs.joined(separator: ","))|\(chunk.text)"
        }
        return ContentHash.hex(text)
    }

    private static func prepareSchema(_ db: SQLiteDatabase) throws {
        try db.execute("PRAGMA busy_timeout = 5000")
        try db.execute("PRAGMA journal_mode = WAL")
        try db.transaction {
            guard try db.scalarInt("PRAGMA user_version") != schemaVersion else { return }
            // Anderes Format (oder neu): Index ist abgeleitet, also einfach neu anlegen.
            try db.execute("""
                DROP TABLE IF EXISTS chunks_fts;
                DROP TABLE IF EXISTS vectors;
                DROP TABLE IF EXISTS chunks;
                DROP TABLE IF EXISTS meetings;
                CREATE TABLE meetings (
                    id TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    indexed_at REAL NOT NULL
                );
                CREATE TABLE chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    meeting_id TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    kind INTEGER NOT NULL,
                    turn_from INTEGER,
                    turn_to INTEGER,
                    start_s REAL NOT NULL,
                    end_s REAL NOT NULL,
                    speaker_ids TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                CREATE INDEX chunks_by_meeting ON chunks(meeting_id);
                CREATE VIRTUAL TABLE chunks_fts USING fts5(
                    text, content = 'chunks', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2'
                );
                CREATE TRIGGER chunks_after_insert AFTER INSERT ON chunks BEGIN
                    INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
                END;
                CREATE TRIGGER chunks_after_delete AFTER DELETE ON chunks BEGIN
                    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
                END;
                CREATE TABLE vectors (
                    chunk_id INTEGER PRIMARY KEY,
                    model TEXT NOT NULL,
                    vector BLOB NOT NULL
                );
                PRAGMA user_version = \(schemaVersion);
                """)
        }
    }
}

public enum SearchIndexError: LocalizedError {
    case embeddingMismatch(expected: Int, got: Int)

    public var errorDescription: String? {
        switch self {
        case .embeddingMismatch(let expected, let got):
            return "Das Embedding-Modell lieferte \(got) statt \(expected) Vektoren."
        }
    }
}
