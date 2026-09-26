import Foundation
import LunaCore
import MCP
import Testing
@testable import LunaMCPServer

/// Ersatz-Embedding (Bag-of-Words mit Synonymen), damit die inhaltliche Suche ohne Modell prüfbar ist.
struct SynonymEmbedder: TextEmbedder {
    let modelID = "test-synonyms"
    let synonyms = ["teurer": "preis", "preise": "preis", "preiserhohung": "preis", "kosten": "preis"]

    func embedDocuments(_ texts: [String]) async throws -> [[Float]] { texts.map(vector) }
    func embedQuery(_ text: String) async throws -> [Float] { vector(text) }

    private func vector(_ text: String) -> [Float] {
        var v = [Float](repeating: 0, count: 32)
        for word in TextFolding.fold(text).split(whereSeparator: { !$0.isLetter }) {
            let key = synonyms[String(word)] ?? String(word)
            v[Int(UInt(bitPattern: key.hashValue) % 32)] += 1
        }
        return v
    }
}

@Suite("Freigabe")
struct AccessTests {
    @Test("Ohne Freigabe: nur lesende Tools sichtbar, jeder Aufruf erklärt, wo man freischaltet")
    func disabled() async throws {
        let harness = try Harness(settings: SharedSettings(mcpAccessEnabled: false, mcpWriteEnabled: true))
        try harness.add(Samples.planning)
        let client = try await harness.connect()

        let tools = try await client.listTools().tools.map(\.name)
        #expect(tools == ["list_meetings", "search_meetings", "get_meeting", "get_processing_status"])

        let result = try await client.call("list_meetings")
        #expect(result.isError)
        #expect(result.text.contains("Meetings für Claude freigeben"))

        #expect(try await client.listResources().resources.count == 3)
        await #expect(throws: (any Error).self) { _ = try await client.readResource(uri: "luna://meetings") }
    }

    @Test("Schreib-Tools erscheinen erst mit eigenem Schalter")
    func writeToggle() async throws {
        let harness = try Harness()
        let client = try await harness.connect()
        #expect(try await client.listTools().tools.count == 4)
        let blocked = try await client.call("rename_meeting", ["id": "abcdef", "title": "x", "revision": "r"])
        #expect(blocked.isError && blocked.text.contains("Claude darf Meetings bearbeiten"))

        harness.settings.mcpWriteEnabled = true
        let tools = try await client.listTools().tools
        #expect(tools.map(\.name).suffix(3) == ["rename_meeting", "rename_speaker", "delete_meetings"])
        #expect(tools.first { $0.name == "delete_meetings" }?.annotations.destructiveHint == true)
        #expect(tools.first { $0.name == "list_meetings" }?.annotations.readOnlyHint == true)
    }
}

@Suite("Lesen")
struct ReadTests {
    @Test("list_meetings: neueste zuerst, vertrauliche unsichtbar, Filter und Seiten")
    func list() async throws {
        let harness = try Harness()
        try harness.add(Samples.planning)
        try harness.add(Samples.retro)
        try harness.add(Samples.secret)
        let client = try await harness.connect()

        let all = try await client.call("list_meetings")
        #expect(!all.isError)
        #expect(all.text.contains("2 Meeting(s)"))
        #expect(!all.text.contains("Personalgespräch"))
        #expect(all.text.firstRange(of: "Planung Q4")!.lowerBound < all.text.firstRange(of: "Retro")!.lowerBound)
        #expect(all.structured?.objectValue?["total"] == 2)

        let page = try await client.call("list_meetings", ["limit": 1])
        #expect(page.structured?.objectValue?["next_cursor"] == "1")

        let older = try await client.call("list_meetings", ["to": "2026-09-20"])
        #expect(older.text.contains("Retro") && !older.text.contains("Planung"))

        let anna = try await client.call("list_meetings", ["speaker": "anna"])
        #expect(anna.text.contains("Planung") && !anna.text.contains("Retro"))

        let bad = try await client.call("list_meetings", ["from": "gestern"])
        #expect(bad.isError && bad.text.contains("JJJJ-MM-TT"))
    }

    @Test("get_meeting: Kopf mit Revision, Sprecherzuordnung, Zusammenfassung, Zeitmarken")
    func getMeeting() async throws {
        let harness = try Harness()
        let folder = try harness.add(Samples.planning)
        let revision = try MeetingLibrary.read(folder: folder).revision
        let client = try await harness.connect()
        let shortID = MeetingLibrary.shortID(Samples.planning.id)

        let result = try await client.call("get_meeting", ["id": .string(shortID)])
        #expect(!result.isError)
        #expect(result.text.contains("# Planung Q4"))
        #expect(result.text.contains("Revision \(revision)"))
        #expect(result.text.contains("Sprecher 1 = Anna"))
        #expect(result.text.contains("### Kurzfassung"))
        #expect(result.text.contains("[00:30] Sprecher 2: Wir müssen die Preise"))
        #expect(harness.activity.first?.action == .read)
        #expect(harness.activity.first?.client == "Testclient")

        let annaOnly = try await client.call("get_meeting", ["id": .string(shortID), "speaker": "Anna", "include": ["transcript"]])
        #expect(!annaOnly.text.contains("Sprecher 2:") && annaOnly.text.contains("gefiltert"))
        #expect(!annaOnly.text.contains("## Zusammenfassung"))

        let secret = try await client.call("get_meeting", ["id": .string(Samples.secret.id.uuidString)])
        #expect(secret.isError && secret.text.contains("Kein Meeting"))

        let tooShort = try await client.call("get_meeting", ["id": "abc"])
        #expect(tooShort.isError && tooShort.text.contains("mindestens die 6 Zeichen"))
    }

    @Test("get_meeting kürzt lange Meetings und nennt die Fortsetzung")
    func pagination() async throws {
        let harness = try Harness()
        let lines = (0 ..< 200).map { ("Sprecher \($0 % 3 + 1)", "Beitrag Nummer \($0) mit etwas Text, damit es sich lohnt.") }
        let long = Samples.meeting("Langes Meeting", lines: lines)
        try harness.add(long)
        let client = try await harness.connect()
        let id = MeetingLibrary.shortID(long.id)

        let first = try await client.call("get_meeting", ["id": .string(id), "max_chars": 2000, "include": ["transcript"]])
        #expect(first.text.count <= 2300)
        let offset = try #require(first.text.firstMatch(of: /offset: (\d+)\)/)).1
        let second = try await client.call("get_meeting", ["id": .string(id), "offset": .int(Int(offset)!), "include": ["transcript"]])
        #expect(second.text.contains("Beiträge \(offset)–200 von 200"))

        let window = try await client.call("get_meeting", ["id": .string(id), "from_time": "10:00", "to_time": "11:00", "include": ["transcript"]])
        #expect(window.text.contains("[10:00]") && !window.text.contains("[09:30]") && !window.text.contains("[11:30]"))
    }

    @Test("search_meetings: Stichwort mit Fundstelle, Zusammenfassung, Hinweis ohne inhaltliche Suche")
    func keywordSearch() async throws {
        let harness = try Harness()
        try harness.add(Samples.planning)
        try harness.add(Samples.retro)
        try harness.add(Samples.secret)
        let client = try await harness.connect()

        let result = try await client.call("search_meetings", ["query": "Preise erhöhen"])
        #expect(!result.isError)
        #expect(result.text.contains("Planung Q4"))
        #expect(result.text.contains("**Preise**"))
        #expect(result.text.contains("Weiterlesen: get_meeting"))
        #expect(result.text.contains("Inhaltliche Suche ist aus"))
        #expect(harness.activity.first?.action == .search)

        let budget = try await client.call("search_meetings", ["query": "Budget"])
        #expect(!budget.text.contains("Personalgespräch"))

        let title = try await client.call("search_meetings", ["query": "Retro"])
        #expect(title.text.contains("Titel/Sprecher"))

        let semanticOnly = try await client.call("search_meetings", ["query": "teurer", "mode": "semantic"])
        #expect(semanticOnly.isError && semanticOnly.text.contains("Inhaltliche Suche ist aus"))
    }

    @Test("search_meetings: inhaltliche Treffer ohne gemeinsames Wort, fusioniert mit Stichworten")
    func semanticSearch() async throws {
        let harness = try Harness(settings: SharedSettings(mcpAccessEnabled: true, semanticSearchEnabled: true),
                                  embedder: SynonymEmbedder())
        try harness.add(Samples.planning)
        try harness.add(Samples.retro)
        let client = try await harness.connect()

        // Erster Aufruf startet die Hintergrund-Berechnung; danach sind alle Abschnitte da.
        _ = try await client.call("search_meetings", ["query": "Kosten"])
        try await Task.sleep(for: .milliseconds(300))
        let result = try await client.call("search_meetings", ["query": "Kosten", "mode": "semantic", "limit": 5])
        #expect(!result.isError)
        #expect(result.text.contains("inhaltlich"))
        #expect(result.text.contains("Retro Sprint 12") || result.text.contains("Planung Q4"))
        #expect(!result.text.contains("indexiert noch"))
    }

    @Test("get_processing_status meldet wartende und fehlgeschlagene Aufträge")
    func status() async throws {
        let harness = try Harness()
        try harness.add(Samples.planning)
        let queue = harness.environment.queueFolder
        let job = MeetingJob(recordedAt: Samples.date, durationSeconds: 600, language: .german, expectedSpeakers: nil,
                             failure: "Modell nicht geladen")
        let folder = queue.appending(path: job.id.uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try JSONCoding.encoder().encode(job).write(to: folder.appending(path: MeetingJob.jobFileName))
        try Data().write(to: folder.appending(path: MeetingJob.audioFileName))
        let client = try await harness.connect()

        let result = try await client.call("get_processing_status")
        #expect(result.text.contains("1 Meeting(s) in der Warteschlange"))
        #expect(result.text.contains("fehlgeschlagen: Modell nicht geladen"))
        #expect(result.text.contains("Zuletzt fertig"))
        #expect(result.text.contains("Suchindex"))
    }
}

@Suite("Schreiben")
struct WriteTests {
    private func writableHarness(confirmRenames: Bool = false) throws -> Harness {
        try Harness(settings: SharedSettings(mcpAccessEnabled: true, mcpWriteEnabled: true, mcpConfirmRenames: confirmRenames))
    }

    @Test("rename_meeting benennt Titel und Ordner um und protokolliert alt/neu")
    func renameMeeting() async throws {
        let harness = try writableHarness()
        let folder = try harness.add(Samples.planning)
        let revision = try MeetingLibrary.read(folder: folder).revision
        let client = try await harness.connect()
        let id = MeetingLibrary.shortID(Samples.planning.id)

        let result = try await client.call("rename_meeting", ["id": .string(id), "title": "Budget Q4", "revision": .string(revision)])
        #expect(!result.isError)
        let stored = try #require(MeetingLibrary.scan(harness.library).first)
        #expect(stored.transcript.title == "Budget Q4")
        #expect(stored.folder.lastPathComponent.contains("Budget Q4"))
        let entry = try #require(harness.activity.first)
        #expect(entry.action == .renameMeeting && entry.oldValue == "Planung Q4" && entry.newValue == "Budget Q4")
        #expect(await harness.confirmer.requests.isEmpty)

        let stale = try await client.call("rename_meeting", ["id": .string(id), "title": "Nochmal", "revision": .string(revision)])
        #expect(stale.isError && stale.text.contains("seit dem Lesen geändert"))
        #expect(try MeetingLibrary.scan(harness.library).first?.transcript.title == "Budget Q4")
    }

    @Test("rename_speaker per Nummer, Name und Zurücksetzen; Beiträge bleiben unverändert")
    func renameSpeaker() async throws {
        let harness = try writableHarness()
        let folder = try harness.add(Samples.planning)
        let client = try await harness.connect()
        let id = MeetingLibrary.shortID(Samples.planning.id)

        var revision = try MeetingLibrary.read(folder: folder).revision
        let named = try await client.call("rename_speaker", ["id": .string(id), "speaker": "2", "name": "Tom", "revision": .string(revision)])
        #expect(!named.isError)
        var stored = try #require(MeetingLibrary.scan(harness.library).first)
        #expect(stored.transcript.speakerNames == ["Sprecher 1": "Anna", "Sprecher 2": "Tom"])
        #expect(stored.transcript.turns == Samples.planning.turns)

        revision = stored.revision
        let reset = try await client.call("rename_speaker", ["id": .string(id), "speaker": "Anna", "name": "", "revision": .string(revision)])
        #expect(!reset.isError)
        stored = try #require(MeetingLibrary.scan(harness.library).first)
        #expect(stored.transcript.speakerNames == ["Sprecher 2": "Tom"])

        let unknown = try await client.call("rename_speaker", ["id": .string(id), "speaker": "Bernd", "name": "X", "revision": .string(stored.revision)])
        #expect(unknown.isError && unknown.text.contains("Vorhanden: Sprecher 1, Sprecher 2 (Tom)"))
    }

    @Test("Bestätigung für Umbenennungen, wenn eingeschaltet – Ablehnen ändert nichts")
    func confirmRenames() async throws {
        let harness = try writableHarness(confirmRenames: true)
        let folder = try harness.add(Samples.planning)
        let client = try await harness.connect()
        let revision = try MeetingLibrary.read(folder: folder).revision

        let declined = try await client.call("rename_meeting", ["id": .string(Samples.planning.id.uuidString), "title": "Neu", "revision": .string(revision)])
        #expect(declined.isError && declined.text.contains("abgelehnt"))
        #expect(await harness.confirmer.requests.count == 1)
        #expect(try MeetingLibrary.read(folder: folder).transcript.title == "Planung Q4")

        await harness.confirmer.setAnswer(.approved)
        let approved = try await client.call("rename_meeting", ["id": .string(Samples.planning.id.uuidString), "title": "Neu", "revision": .string(revision)])
        #expect(!approved.isError)
    }

    @Test("delete_meetings: Ablehnen löscht nichts und sperrt die Wiederholung")
    func deleteDeclined() async throws {
        let harness = try writableHarness()
        let folder = try harness.add(Samples.planning)
        let revision = try MeetingLibrary.read(folder: folder).revision
        let client = try await harness.connect()
        let items: Value = [["id": .string(Samples.planning.id.uuidString), "revision": .string(revision)]]

        let result = try await client.call("delete_meetings", ["items": items])
        #expect(!result.isError)
        #expect(result.text.contains("abgelehnt"))
        #expect(FileManager.default.fileExists(atPath: folder.path))
        #expect(harness.activity.first?.action == .deleteDeclined)

        let requests = await harness.confirmer.requests
        guard case .delete(let described)? = requests.first else { Issue.record("Kein Lösch-Dialog"); return }
        #expect(described.map(\.title) == ["Planung Q4"])
        #expect(requests[0].message(client: "Testclient").contains("Papierkorb"))
        #expect(await harness.confirmer.clients == ["Testclient"])

        let again = try await client.call("delete_meetings", ["items": items])
        #expect(again.isError && again.text.contains("gerade abgelehnt"))
        #expect(await harness.confirmer.requests.count == 1)
    }

    @Test("delete_meetings: nach Zustimmung im Papierkorb, mehrere in einem Dialog")
    func deleteApproved() async throws {
        let harness = try writableHarness()
        let first = try harness.add(Samples.planning)
        let second = try harness.add(Samples.retro)
        await harness.confirmer.setAnswer(.approved)
        let client = try await harness.connect()

        let items: Value = .array([first, second].map { folder in
            let stored = try! MeetingLibrary.read(folder: folder)
            return ["id": .string(MeetingLibrary.shortID(stored.id)), "revision": .string(stored.revision)]
        })
        let result = try await client.call("delete_meetings", ["items": items])
        #expect(!result.isError)
        #expect(result.text.contains("In den Papierkorb verschoben"))
        #expect(MeetingLibrary.scan(harness.library).isEmpty)
        #expect(await harness.confirmer.requests.count == 1)
        #expect(harness.activity.first?.action == .delete && harness.activity.first?.meetingIDs.count == 2)

        let list = try await client.call("list_meetings")
        #expect(list.text.contains("noch keine"))
    }

    @Test("delete_meetings: ohne Dialog (keine grafische Sitzung) passiert nichts")
    func deleteUnavailable() async throws {
        let harness = try writableHarness()
        let folder = try harness.add(Samples.planning)
        await harness.confirmer.setAnswer(.unavailable("Keine grafische Sitzung."))
        let client = try await harness.connect()
        let revision = try MeetingLibrary.read(folder: folder).revision

        let result = try await client.call("delete_meetings", ["items": [["id": .string(Samples.planning.id.uuidString), "revision": .string(revision)]]])
        #expect(result.isError && result.text.contains("Nichts wurde gelöscht"))
        #expect(FileManager.default.fileExists(atPath: folder.path))
    }

    @Test("delete_meetings: veraltete Revision → kein Dialog")
    func deleteStale() async throws {
        let harness = try writableHarness()
        try harness.add(Samples.planning)
        let client = try await harness.connect()
        let result = try await client.call("delete_meetings", ["items": [["id": .string(Samples.planning.id.uuidString), "revision": "0000"]]])
        #expect(result.isError && result.text.contains("seit dem Lesen geändert"))
        #expect(await harness.confirmer.requests.isEmpty)
    }

    @Test("Bremse: höchstens 3 Lösch-Dialoge in 10 Minuten")
    func deleteThrottle() async throws {
        let guardian = ConfirmationGuard()
        let now = Date()
        for _ in 0 ..< 3 {
            try await guardian.begin(deleting: [UUID()], now: now)
            await guardian.end(now: now)
        }
        await #expect(throws: ToolError.self) { try await guardian.begin(deleting: [UUID()], now: now) }
        try await guardian.begin(deleting: [UUID()], now: now.addingTimeInterval(601))
        await #expect(throws: ToolError.self) { try await guardian.begin(deleting: nil, now: now) }
    }
}

@Suite("Ressourcen und Prompts")
struct ResourcePromptTests {
    @Test("Liste, Vorlagen, Lesen, Wörterbuch, Vervollständigung")
    func resources() async throws {
        var settings = SharedSettings(mcpAccessEnabled: true)
        settings.vocabularyEntries = [VocabularyEntry(term: "LunaTranscribe", aliases: ["Luna Transcribe"])]
        let harness = try Harness(settings: settings)
        try harness.add(Samples.planning)
        try harness.add(Samples.secret)
        let client = try await harness.connect()
        let id = MeetingLibrary.shortID(Samples.planning.id)

        let list = try await client.listResources().resources
        #expect(list.map(\.uri).contains("luna://meetings/\(Samples.planning.id.uuidString.lowercased())"))
        #expect(!list.contains { $0.uri.contains(Samples.secret.id.uuidString.lowercased()) })
        #expect(try await client.listResourceTemplates().templates.count == 3)

        let meeting = try await client.readResource(uri: "luna://meetings/\(id)")
        #expect(meeting.first?.text?.contains("# Planung Q4") == true)
        let summary = try await client.readResource(uri: "luna://meetings/\(id)/summary")
        #expect(summary.first?.text?.contains("## Transkript") == false)
        let json = try await client.readResource(uri: "luna://meetings/\(id)/transcript.json")
        #expect(json.first?.mimeType == "application/json")
        let vocabulary = try await client.readResource(uri: "luna://vocabulary")
        #expect(vocabulary.first?.text?.contains("**LunaTranscribe** (falsch erkannt als: Luna Transcribe)") == true)
        await #expect(throws: (any Error).self) { _ = try await client.readResource(uri: "luna://meetings/\(Samples.secret.id.uuidString)") }

        let completion = try await client.complete(resourceURI: "luna://meetings/{id}", argumentName: "id", argumentValue: "plan")
        #expect(completion.values == [id])
    }

    @Test("Prompts: Liste, Zusammenfassung mit eingebettetem Meeting und App-Anweisung, Wochenrückblick")
    func prompts() async throws {
        let harness = try Harness()
        try harness.add(Samples.planning)
        let client = try await harness.connect()

        #expect(try await client.listPrompts().prompts.map(\.name) ==
            ["meeting_summary", "weekly_review", "action_items", "follow_up_email", "find_decision", "name_speakers"])

        let summary = try await client.getPrompt(name: "meeting_summary", arguments: ["id": MeetingLibrary.shortID(Samples.planning.id)])
        guard case .resource(let resource, _, _) = summary.messages.first?.content else { Issue.record("Kein eingebettetes Meeting"); return }
        #expect(resource.text?.contains("# Planung Q4") == true)
        guard case .text(let instructions) = summary.messages.last?.content else { Issue.record("Keine Anweisung"); return }
        #expect(instructions.contains("## Kurzfassung") && instructions.contains("auf Deutsch"))

        let week = try await client.getPrompt(name: "weekly_review", arguments: ["week": "2026-W39"])
        guard case .text(let text) = week.messages.first?.content else { Issue.record("Kein Text"); return }
        #expect(text.contains("vom 21.09.2026 bis 27.09.2026"))
        #expect(text.contains("list_meetings(from: \"2026-09-21\", to: \"2026-09-27\")"))

        await #expect(throws: (any Error).self) { _ = try await client.getPrompt(name: "weekly_review", arguments: ["week": "irgendwann"]) }
    }
}
