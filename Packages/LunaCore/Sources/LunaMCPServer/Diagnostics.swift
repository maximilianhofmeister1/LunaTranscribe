import Foundation
import LunaCore
import MCP

/// Prüfungen für `luna-mcp --selftest` / `--check` und „Verbindung testen“ in der App. Liefern Text statt
/// zu drucken, damit der Aufrufer entscheidet, wohin (im MCP-Betrieb gehört stdout nur JSON-RPC).
public enum Diagnostics {
    /// Prüft die echte Einrichtung, nur lesend: Ordner, Meetings, Schalter, Suchindex, Modell.
    public static func check(environment: ServerEnvironment, modelStatus: String) async -> String {
        let settings = environment.settings()
        let folder = environment.libraryFolder(settings)
        let reachable = FileManager.default.fileExists(atPath: folder.path)
        let all = MeetingLibrary.scan(folder) + MeetingLibrary.scan(environment.unsavedFolder, unsaved: true)
        let confidential = all.filter(\.transcript.confidential).count
        let server = LunaMCPServer(environment: environment)
        let context = await server.makeContext()

        func onOff(_ value: Bool) -> String { value ? "an" : "aus" }
        var lines = [
            "luna-mcp \(environment.version)",
            "Meeting-Ordner: \(folder.path) (\(reachable ? "erreichbar" : "NICHT erreichbar"))",
            "Meetings: \(all.count)" + (confidential > 0 ? " (davon \(confidential) vertraulich – für Claude unsichtbar)" : ""),
            "Freigabe für Claude: \(onOff(settings.mcpAccessEnabled)) · Bearbeiten: \(onOff(settings.mcpWriteEnabled)) · Umbenennungen bestätigen: \(onOff(settings.mcpConfirmRenames))",
            "Inhaltliche Suche: \(onOff(settings.semanticSearchEnabled)) · \(modelStatus)",
        ]
        lines.append(await server.search.stats(snapshot: context.snapshot, settings: settings))
        return lines.joined(separator: "\n")
    }

    /// Funktionsprobe gegen eine temporäre Beispiel-Bibliothek: Handshake, jedes Tool, Ressourcen, Prompts.
    /// Löschen wird nur mit „Ablehnen“ geprüft – eine echte Zustimmung legte Ordner in den Papierkorb des Nutzers.
    public static func selfTest(embedder: (any TextEmbedder)?, version: String) async -> (passed: Bool, report: String) {
        var report: [String] = []
        var passed = true
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            passed = passed && ok
            report.append("\(ok ? "✓" : "✗") \(name)\(detail.isEmpty ? "" : " – \(detail)")")
        }

        let root = FileManager.default.temporaryDirectory.appending(path: "luna-mcp-selftest-\(UUID().uuidString.prefix(8))", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: root) }
        let library = root.appending(path: "Library", directoryHint: .isDirectory)
        do {
            for meeting in SampleLibrary.meetings { try MeetingLibrary.write(meeting, into: library, existing: nil) }
        } catch {
            return (false, "✗ Beispiel-Bibliothek anlegen – \(error.localizedDescription)")
        }

        let settings = SharedSettings(mcpAccessEnabled: true, mcpWriteEnabled: true, semanticSearchEnabled: embedder != nil)
        let environment = ServerEnvironment(
            settings: { settings }, libraryOverride: library,
            unsavedFolder: root.appending(path: "Unsaved", directoryHint: .isDirectory),
            queueFolder: root.appending(path: "Queue", directoryHint: .isDirectory),
            indexURL: root.appending(path: "search.sqlite"),
            activityLog: ActivityLog(url: root.appending(path: "activity.log")),
            embedder: { embedder }, confirmer: FixedConfirmer(outcome: .declined),
            version: version, changePollInterval: nil)
        let server = LunaMCPServer(environment: environment)
        let (clientTransport, serverTransport) = await InMemoryTransport.createConnectedPair()
        let serverTask = Task { try await server.run(transport: serverTransport) }
        defer { serverTask.cancel() }
        let client = Client(name: "luna-mcp-selftest", version: version)

        do {
            let start = Date()
            let initialize = try await client.connect(transport: clientTransport)
            check("Handshake", initialize.serverInfo.name == LunaMCPServer.name, "Protokoll \(initialize.protocolVersion)")
            let tools = try await client.listTools().tools.map(\.name)
            check("Tools", tools.count == 7, tools.joined(separator: ", "))

            let list = try await client.callTool(name: "list_meetings")
            check("list_meetings", text(list.content).contains("3 Meeting(s)") && !text(list.content).contains(SampleLibrary.secretTitle),
                  "vertrauliches Meeting unsichtbar")

            let keyword = try await client.callTool(name: "search_meetings", arguments: ["query": "Angebot", "mode": "keyword"])
            check("Stichwortsuche", text(keyword.content).contains(SampleLibrary.planningTitle))

            if embedder != nil {
                _ = try await client.callTool(name: "search_meetings", arguments: ["query": "Kosten"])
                await server.search.waitForEmbedding()
                let semantic = try await client.callTool(name: "search_meetings", arguments: ["query": "Wir müssen mehr Geld verlangen", "mode": "semantic", "limit": 1])
                check("Inhaltliche Suche", text(semantic.content).contains(SampleLibrary.planningTitle),
                      "„mehr Geld verlangen“ findet die Preiserhöhung")
            } else {
                report.append("– Inhaltliche Suche übersprungen (Modell nicht geladen)")
            }

            let id = MeetingLibrary.shortID(SampleLibrary.meetings[0].id)
            let meeting = try await client.callTool(name: "get_meeting", arguments: ["id": .string(id)])
            let revision = text(meeting.content).firstMatch(of: /Revision ([0-9a-f]+)/).map { String($0.1) } ?? ""
            check("get_meeting", !revision.isEmpty && text(meeting.content).contains("[00:30]"))

            let renamed = try await client.callTool(name: "rename_speaker", arguments: ["id": .string(id), "speaker": "2", "name": "Tom", "revision": .string(revision)])
            check("rename_speaker", renamed.isError != true)
            let stale = try await client.callTool(name: "rename_meeting", arguments: ["id": .string(id), "title": "X", "revision": .string(revision)])
            check("Revisionsschutz", stale.isError == true, "veraltete Revision abgelehnt")

            let current = MeetingLibrary.scan(library).first { $0.id == SampleLibrary.meetings[0].id }?.revision ?? ""
            let delete = try await client.callTool(name: "delete_meetings", arguments: ["items": [["id": .string(id), "revision": .string(current)]]])
            check("delete_meetings", text(delete.content).contains("abgelehnt") && MeetingLibrary.scan(library).count == SampleLibrary.meetings.count,
                  "abgelehnt → nichts gelöscht")

            let resource = try await client.readResource(uri: "luna://meetings/\(id)")
            check("Ressourcen", resource.first?.text?.contains("# ") == true)
            let prompt = try await client.getPrompt(name: "meeting_summary", arguments: ["id": id])
            check("Prompts", prompt.messages.count == 2)
            report.append(String(format: "Dauer: %.2f s", Date().timeIntervalSince(start)))
        } catch {
            check("Protokoll", false, error.localizedDescription)
        }

        if let embedder {
            let (ok, detail) = await embeddingSanity(embedder)
            check("Embedding-Modell", ok, detail)
        }
        await client.disconnect()
        return (passed, report.joined(separator: "\n"))
    }

    /// Plausibilität der Vektoren: Ein Satz muss seiner Umschreibung näher sein als einem fremden Satz.
    /// Fängt ein falsch geladenes Modell ab (falsche Positionen, falsches Pooling), das sonst nur still schlechter sucht.
    public static func embeddingSanity(_ embedder: any TextEmbedder) async -> (Bool, String) {
        do {
            let query = try await embedder.embedQuery("Wir müssen die Preise erhöhen.")
            let documents = try await embedder.embedDocuments(["Die Kosten steigen, wir sollten teurer werden.",
                                                               "Morgen soll es in Hamburg regnen."])
            func cosine(_ a: [Float], _ b: [Float]) -> Float {
                let dot = zip(a, b).reduce(0) { $0 + $1.0 * $1.1 }
                let norm = (a.reduce(0) { $0 + $1 * $1 } * b.reduce(0) { $0 + $1 * $1 }).squareRoot()
                return norm > 0 ? dot / norm : 0
            }
            let related = cosine(query, documents[0])
            let unrelated = cosine(query, documents[1])
            let detail = String(format: "%@, %d Dimensionen, ähnlich %.3f vs. fremd %.3f", embedder.modelID, query.count, related, unrelated)
            return (related > unrelated + 0.02, detail)
        } catch {
            return (false, error.localizedDescription)
        }
    }

    private static func text(_ content: [Tool.Content]) -> String {
        content.compactMap { if case .text(let text, _, _) = $0 { return text } else { return nil } }.joined(separator: "\n")
    }
}

/// Bestätiger mit fester Antwort (Selbsttest).
struct FixedConfirmer: UserConfirmer {
    let outcome: ConfirmationOutcome
    func confirm(_ request: ConfirmationRequest, client: String) async -> ConfirmationOutcome { outcome }
}

/// Beispiel-Meetings für den Selbsttest (keine echten Daten).
enum SampleLibrary {
    static let planningTitle = "Selbsttest: Planung Q4"
    static let secretTitle = "Selbsttest: Vertraulich"

    static let meetings: [MeetingTranscript] = {
        let date = Date(timeIntervalSince1970: 1_790_000_000)
        func make(_ title: String, _ offset: TimeInterval, _ lines: [(String, String)], confidential: Bool = false) -> MeetingTranscript {
            let turns = lines.enumerated().map { index, line in
                MeetingTranscript.Turn(speakerID: line.0, start: TimeInterval(index * 30), end: TimeInterval(index * 30 + 25), text: line.1)
            }
            var meeting = MeetingTranscript(title: title, date: date.addingTimeInterval(offset), durationSeconds: TimeInterval(lines.count * 30),
                                            engine: .parakeet, language: .german, turns: turns)
            meeting.isConfidential = confidential ? true : nil
            return meeting
        }
        return [
            make(planningTitle, 0, [
                ("Sprecher 1", "Dann lass uns mit dem Budget für das vierte Quartal anfangen."),
                ("Sprecher 2", "Die Preise müssen im nächsten Quartal um zehn Prozent erhöht werden."),
                ("Sprecher 1", "Einverstanden. Tom holt bis Freitag ein Angebot vom Lieferanten ein."),
            ]),
            make("Selbsttest: Retro", -86_400, [
                ("Sprecher 1", "Das Deployment am Freitag war holprig, die Tests liefen zu spät."),
                ("Sprecher 2", "Nächstes Mal frieren wir den Code am Mittwoch ein."),
            ]),
            make("Selbsttest: Wetter", -172_800, [("Sprecher 1", "Morgen soll es regnen, der Ausflug fällt aus.")]),
            make(secretTitle, -3_600, [("Sprecher 1", "Gehaltsgespräch, streng vertraulich.")], confidential: true),
        ]
    }()
}
