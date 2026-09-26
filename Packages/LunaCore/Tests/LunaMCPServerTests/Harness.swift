import Foundation
import LunaCore
import MCP
@testable import LunaMCPServer

/// Startet einen echten `LunaMCPServer` gegen eine temporäre Bibliothek und verbindet einen echten
/// MCP-Client über den In-Memory-Transport des SDK – also derselbe Protokollweg wie über stdio.
final class Harness: @unchecked Sendable {
    let root: URL
    let library: URL
    let environment: ServerEnvironment
    let confirmer = ScriptedConfirmer()
    private let settingsBox: SettingsBox
    private var serverTask: Task<Void, any Error>?

    /// Einstellungen „der App“; Änderungen wirken bei der nächsten Anfrage.
    var settings: SharedSettings {
        get { settingsBox.value }
        set { settingsBox.value = newValue }
    }

    init(settings: SharedSettings = SharedSettings(mcpAccessEnabled: true), embedder: (any TextEmbedder)? = nil) throws {
        root = FileManager.default.temporaryDirectory.appending(path: "LunaMCPTests-\(UUID().uuidString.prefix(8))", directoryHint: .isDirectory)
        library = root.appending(path: "Library", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: library, withIntermediateDirectories: true)
        let box = SettingsBox(settings)
        settingsBox = box
        environment = ServerEnvironment(
            settings: { box.value },
            libraryOverride: library,
            unsavedFolder: root.appending(path: "Unsaved", directoryHint: .isDirectory),
            queueFolder: root.appending(path: "Queue", directoryHint: .isDirectory),
            indexURL: root.appending(path: "Index/search.sqlite"),
            activityLog: ActivityLog(url: root.appending(path: "activity.log")),
            embedder: { embedder },
            confirmer: confirmer,
            timeZone: TimeZone(identifier: "Europe/Berlin")!,
            version: "test",
            changePollInterval: nil)
    }

    @discardableResult
    func add(_ meeting: MeetingTranscript) throws -> URL {
        try MeetingLibrary.write(meeting, into: library, existing: nil)
    }

    func connect() async throws -> Client {
        let (clientTransport, serverTransport) = await InMemoryTransport.createConnectedPair()
        let server = LunaMCPServer(environment: environment)
        serverTask = Task { try await server.run(transport: serverTransport) }
        let client = Client(name: "test-client", version: "1.0", title: "Testclient")
        _ = try await client.connect(transport: clientTransport)
        return client
    }

    var activity: [ActivityEntry] { environment.activityLog.entries() }

    deinit {
        serverTask?.cancel()
        try? FileManager.default.removeItem(at: root)
    }
}

final class SettingsBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: SharedSettings

    init(_ value: SharedSettings) { stored = value }

    var value: SharedSettings {
        get { lock.withLock { stored } }
        set { lock.withLock { stored = newValue } }
    }
}

/// Bestätigungsdialog-Ersatz: liefert vorgegebene Antworten und merkt sich die Anfragen.
actor ScriptedConfirmer: UserConfirmer {
    private(set) var requests: [ConfirmationRequest] = []
    private(set) var clients: [String] = []
    var answer: ConfirmationOutcome = .declined

    func setAnswer(_ outcome: ConfirmationOutcome) { answer = outcome }

    func confirm(_ request: ConfirmationRequest, client: String) async -> ConfirmationOutcome {
        requests.append(request)
        clients.append(client)
        return answer
    }
}

enum Samples {
    static let date = ISO8601DateFormatter().date(from: "2026-09-22T12:00:00Z")!

    static func meeting(_ title: String, daysAgo: Double = 0, lines: [(String, String)], summary: String? = nil,
                        names: [String: String] = [:], confidential: Bool = false) -> MeetingTranscript {
        let turns = lines.enumerated().map { index, line in
            MeetingTranscript.Turn(speakerID: line.0, start: TimeInterval(index * 30), end: TimeInterval(index * 30 + 25), text: line.1)
        }
        var meeting = MeetingTranscript(title: title, date: date.addingTimeInterval(-daysAgo * 86_400),
                                        durationSeconds: TimeInterval(lines.count * 30), engine: .parakeet,
                                        language: .german, turns: turns, speakerNames: names)
        meeting.summary = summary.map { MeetingSummary(markdown: $0, createdAt: date) }
        meeting.isConfidential = confidential ? true : nil
        return meeting
    }

    static let planning = meeting("Planung Q4", lines: [
        ("Sprecher 1", "Dann lass uns mit dem Budget anfangen."),
        ("Sprecher 2", "Wir müssen die Preise im nächsten Quartal erhöhen, sonst reicht das Budget nicht."),
        ("Sprecher 1", "Einverstanden, Tom kümmert sich bis Freitag um das Angebot."),
    ], summary: "## Kurzfassung\nPreiserhöhung beschlossen.\n\n## Aufgaben\n- [ ] Tom: Angebot bis Freitag", names: ["Sprecher 1": "Anna"])

    static let retro = meeting("Retro Sprint 12", daysAgo: 3, lines: [
        ("Sprecher 1", "Das Deployment am Freitag war holprig."),
        ("Sprecher 2", "Ehrlich gesagt sollten wir teurer werden."),
    ])

    static let secret = meeting("Personalgespräch", daysAgo: 1, lines: [("Sprecher 1", "Gehalt und Budget.")], confidential: true)
}

extension Client {
    /// Tool aufrufen und Text + Fehlerflag + strukturierte Ausgabe zurückgeben.
    func call(_ name: String, _ arguments: [String: Value] = [:]) async throws -> (text: String, isError: Bool, structured: Value?) {
        let context: RequestContext<CallTool.Result> = try send(CallTool.request(.init(name: name, arguments: arguments)))
        let result = try await context.value
        let text = result.content.compactMap { content -> String? in
            if case .text(let text, _, _) = content { return text }
            return nil
        }.joined(separator: "\n")
        return (text, result.isError ?? false, result.structuredContent)
    }
}
