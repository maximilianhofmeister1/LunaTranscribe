import Foundation
import Testing
@testable import LunaCore

@Suite("Chunking")
struct ChunkerTests {
    @Test("Kurze Beiträge landen zusammen in einem Abschnitt")
    func shortMeetingIsOneChunk() {
        let meeting = Support.meeting(lines: [("Sprecher 1", "Hallo."), ("Sprecher 2", "Hi."), ("Sprecher 1", "Los geht's.")])
        let chunks = Chunker.chunks(for: meeting)
        #expect(chunks.count == 1)
        #expect(chunks[0].turns == 0 ... 2)
        #expect(chunks[0].speakerIDs == ["Sprecher 1", "Sprecher 2"])
    }

    @Test("Lange Meetings werden mit Überlappung geteilt, nichts geht verloren")
    func longMeetingOverlaps() {
        let sentence = Array(repeating: "wort", count: 70).joined(separator: " ")
        let meeting = Support.meeting(lines: (0 ..< 10).map { ("Sprecher \($0 % 2 + 1)", sentence) })
        let chunks = Chunker.chunks(for: meeting)
        #expect(chunks.count > 1)
        #expect(chunks.allSatisfy { Chunker.wordCount($0.text) <= Chunker.targetWords + 70 })
        let covered = Set(chunks.flatMap { Array($0.turns!) })
        #expect(covered == Set(0 ..< 10))
        #expect(chunks.last?.turns?.upperBound == 9)
    }

    @Test("Ein überlanger Beitrag wird an Satzgrenzen geteilt")
    func splitsLongTurn() {
        let sentence = Array(repeating: "wort", count: 30).joined(separator: " ") + ". "
        let meeting = Support.meeting(lines: [("Sprecher 1", String(repeating: sentence, count: 20))])
        let chunks = Chunker.chunks(for: meeting)
        #expect(chunks.count >= 3)
        #expect(chunks.allSatisfy { $0.turns == 0 ... 0 })
    }

    @Test("Zusammenfassung wird nach Überschriften zerlegt")
    func summarySections() {
        let meeting = Support.meeting(lines: [("Sprecher 1", "Text")], summary: "## Kurzfassung\nA\n\n## Aufgaben\n- [ ] B")
        let summary = Chunker.chunks(for: meeting).filter { $0.kind == .summary }
        #expect(summary.map(\.text) == ["## Kurzfassung\nA", "## Aufgaben\n- [ ] B"])
    }
}

@Suite("Stichwort-Anfrage")
struct KeywordQueryTests {
    @Test("Füllwörter fallen weg, lange Wörter suchen als Präfix")
    func naturalQuestion() {
        let query = KeywordQuery("Wo haben wir über die Preise gesprochen?")
        #expect(query.all == "\"preise\"*")
        #expect(query.any == nil)
    }

    @Test("Phrasen und Umlaute")
    func phrases() {
        let query = KeywordQuery("\u{201E}nächstes Quartal\u{201C} Müller")
        #expect(query.all == "\"nachstes quartal\" AND \"muller\"*")
        #expect(query.any == "\"nachstes quartal\" OR \"muller\"*")
    }

    @Test("Wörterbuch-Varianten werden gemeinsam gesucht")
    func vocabularyExpansion() {
        let vocabulary = [VocabularyEntry(term: "LunaTranscribe", aliases: ["Luna Transcribe", "Luna transkribe"])]
        let query = KeywordQuery("Luna Transcribe Release", vocabulary: vocabulary)
        #expect(query.all == "(\"luna transcribe\" OR \"luna transkribe\" OR \"lunatranscribe\") AND \"release\"*")
    }

    @Test("ß bleibt erhalten wie im FTS-Tokenizer")
    func eszett() {
        #expect(KeywordQuery("Straße").all == "\"straße\"*")
    }
}

@Suite("Suchindex")
struct SearchIndexTests {
    private func library() -> [StoredMeeting] {
        let budget = Support.meeting(title: "Planung Q4", lines: [
            ("Sprecher 1", "Dann lass uns mit dem Budget anfangen."),
            ("Sprecher 2", "Wir müssen die Preise im nächsten Quartal erhöhen."),
        ], summary: "## Aufgaben\n- [ ] Tom: Angebot einholen")
        let retro = Support.meeting(title: "Retro", lines: [
            ("Sprecher 1", "Das Deployment am Freitag war holprig."),
            ("Sprecher 2", "Wir sollten teurer werden, ehrlich gesagt."),
        ])
        let secret = Support.meeting(title: "Personalgespräch", lines: [("Sprecher 1", "Budget und Gehalt.")], confidential: true)
        return [budget, retro, secret].map {
            StoredMeeting(transcript: $0, folder: URL(filePath: "/tmp/\($0.id)"), revision: "r", hasAudio: false, isUnsaved: false)
        }
    }

    @Test("Abgleich ist idempotent und lässt vertrauliche Meetings weg")
    func syncIdempotent() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        let meetings = library()
        let first = try await index.sync(meetings)
        #expect(first == SearchIndex.SyncReport(added: 2, updated: 0, removed: 0, unchanged: 0))
        let second = try await index.sync(meetings)
        #expect(second == SearchIndex.SyncReport(added: 0, updated: 0, removed: 0, unchanged: 2))

        var changed = meetings
        var transcript = changed[0].transcript
        transcript.turns[0].text = "Neuer Text"
        changed[0] = StoredMeeting(transcript: transcript, folder: changed[0].folder, revision: "r2", hasAudio: false, isUnsaved: false)
        let third = try await index.sync(Array(changed.prefix(1)))
        #expect(third == SearchIndex.SyncReport(added: 0, updated: 1, removed: 1, unchanged: 0))
    }

    @Test("Umbenennen (Titel, Sprecher) ändert den Index nicht")
    func renameDoesNotReindex() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        var meetings = library()
        try await index.sync(meetings)
        var transcript = meetings[0].transcript
        transcript.title = "Anders"
        transcript.speakerNames["Sprecher 1"] = "Anna"
        meetings[0] = StoredMeeting(transcript: transcript, folder: meetings[0].folder, revision: "r2", hasAudio: false, isUnsaved: false)
        let report = try await index.sync(meetings)
        #expect(!report.hasChanges)
    }

    @Test("Stichwortsuche mit Präfix, Umlaut und Fundstellen-Markierung")
    func keywordSearch() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        let meetings = library()
        try await index.sync(meetings)

        let hits = try await index.keywordSearch(KeywordQuery("Preis erhöhen"), limit: 5)
        #expect(hits.count == 1)
        #expect(hits[0].record.meetingID == meetings[0].id)
        #expect(hits[0].snippet.contains("**Preise**"))

        // Vertrauliches Meeting nie im Index.
        #expect(try await index.keywordSearch(KeywordQuery("Gehalt"), limit: 5).isEmpty)
        // Zusammenfassung ist durchsuchbar.
        #expect(try await index.keywordSearch(KeywordQuery("Angebot"), limit: 5).first?.record.kind == .summary)
    }

    @Test("ODER-Rückfallebene, wenn UND nichts findet")
    func orFallback() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        try await index.sync(library())
        let hits = try await index.keywordSearch(KeywordQuery("Deployment Budget"), limit: 5)
        #expect(hits.count == 2)
    }

    @Test("Vektorsuche und Nachberechnen fehlender Vektoren")
    func vectorSearch() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        let meetings = library()
        try await index.sync(meetings)
        let embedder = FakeEmbedder()
        let embedded = try await index.embedPending(with: embedder)
        #expect(embedded == 3)                    // 2 Meetings à 1 Abschnitt + 1 Zusammenfassungsabschnitt
        #expect(try await index.embedPending(with: embedder) == 0)
        #expect(try await index.stats(model: embedder.modelID) == SearchIndex.Stats(meetings: 2, chunks: 3, embedded: 3))

        let query = try await embedder.embedQuery("kosten")
        let hits = try await index.vectorSearch(query, model: embedder.modelID, limit: 1)
        #expect(hits.first?.record.meetingID != nil)

        // Anderes Modell: alte Vektoren weg, neu rechnen.
        var other = FakeEmbedder()
        other.modelID = "fake-2"
        #expect(try await index.embedPending(with: other) == 3)
        #expect(try await index.vectorSearch(query, model: embedder.modelID, limit: 3).isEmpty)
    }

    @Test("Fusion: gemeinsame Treffer vorne, höchstens n je Meeting")
    func fusion() async throws {
        let index = try SearchIndex(url: try Support.temporaryFolder().appending(path: "i.sqlite"))
        let meetings = library()
        try await index.sync(meetings)
        let embedder = FakeEmbedder()
        try await index.embedPending(with: embedder)

        let keyword = try await index.keywordSearch(KeywordQuery("Preise"), limit: 10)
        let semantic = try await index.vectorSearch(try await embedder.embedQuery("Preise"), model: embedder.modelID, limit: 10)
        let fused = HybridRanker.fuse(keyword: keyword, semantic: semantic, metadata: [meetings[1].id], perMeeting: 1, limit: 10)
        #expect(fused.first?.sources == [.keyword, .semantic])
        #expect(Set(fused.map(\.meetingID)).count == fused.count)
    }

    @Test("Index überlebt Schließen und Wiederöffnen (zweiter Prozess)")
    func reopen() async throws {
        let url = try Support.temporaryFolder().appending(path: "i.sqlite")
        let meetings = library()
        do {
            let index = try SearchIndex(url: url)
            try await index.sync(meetings)
        }
        let again = try SearchIndex(url: url)
        #expect(try await again.stats().meetings == 2)
        let other = try SearchIndex(url: url)   // parallele Verbindung
        #expect(!(try await other.sync(meetings).hasChanges))
    }
}
