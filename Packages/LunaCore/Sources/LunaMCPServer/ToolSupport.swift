import Foundation
import LunaCore
import MCP

/// Alles, was ein Tool-Aufruf braucht – pro Anfrage frisch (Einstellungen, Katalog-Stand, Client-Name).
struct ToolContext: Sendable {
    let environment: ServerEnvironment
    let settings: SharedSettings
    let snapshot: MeetingCatalog.Snapshot
    let search: SearchService
    let guardian: ConfirmationGuard
    let audit: AuditTrail
    let client: String

    var now: Date { environment.now() }

    func record(_ action: ActivityEntry.Action, _ ids: [UUID], speakerID: String? = nil,
                oldValue: String? = nil, newValue: String? = nil) async {
        await audit.record(ActivityEntry(date: now, client: client, action: action, meetingIDs: ids,
                                         speakerID: speakerID, oldValue: oldValue, newValue: newValue))
    }
}

/// Ein Tool: Definition für `tools/list` plus Ausführung.
struct ToolHandler: Sendable {
    let tool: Tool
    /// Nur sichtbar und aufrufbar, wenn der Nutzer Änderungen erlaubt hat.
    let writes: Bool
    let run: @Sendable (Arguments, ToolContext) async throws -> CallTool.Result
}

/// Schreibt das Aktivitätsprotokoll der Reihe nach (mehrere Tool-Aufrufe können gleichzeitig laufen).
/// Ein Fehler beim Protokollieren darf keinen Aufruf scheitern lassen.
actor AuditTrail {
    private let log: ActivityLog

    init(log: ActivityLog) { self.log = log }

    func record(_ entry: ActivityEntry) {
        try? log.append(entry)
    }
}

/// Kurzschreibweisen für JSON-Schemas.
enum Schema {
    static func object(_ properties: [String: Value], required: [String] = []) -> Value {
        var schema: [String: Value] = ["type": "object", "properties": .object(properties), "additionalProperties": false]
        if !required.isEmpty { schema["required"] = .array(required.map { .string($0) }) }
        return .object(schema)
    }

    static func string(_ description: String, oneOf values: [String]? = nil) -> Value {
        var schema: [String: Value] = ["type": "string", "description": .string(description)]
        if let values { schema["enum"] = .array(values.map { .string($0) }) }
        return .object(schema)
    }

    static func integer(_ description: String, range: ClosedRange<Int>) -> Value {
        ["type": "integer", "description": .string(description),
         "minimum": .int(range.lowerBound), "maximum": .int(range.upperBound)]
    }

    static func boolean(_ description: String) -> Value {
        ["type": "boolean", "description": .string(description)]
    }

    static func array(_ items: Value, _ description: String, count: ClosedRange<Int>? = nil) -> Value {
        var schema: [String: Value] = ["type": "array", "items": items, "description": .string(description)]
        if let count {
            schema["minItems"] = .int(count.lowerBound)
            schema["maxItems"] = .int(count.upperBound)
        }
        return .object(schema)
    }

    static let meetingID = string("Meeting-ID: die volle ID oder mindestens die ersten 6 Zeichen (wie in list_meetings).")
    static let revision = string("Revision aus get_meeting oder list_meetings. Schützt davor, eine inzwischen geänderte Fassung zu überschreiben.")
    static let from = string("Frühestes Datum, JJJJ-MM-TT (inklusiv, Zeitzone des Nutzers).")
    static let to = string("Spätestes Datum, JJJJ-MM-TT (inklusiv).")
    static let speaker = string("Nur Meetings/Beiträge dieses Sprechers (Name oder „Sprecher 2“).")
}

/// Strukturierte Ausgabe eines Meetings (für `structuredContent`).
struct MeetingInfo: Codable, Sendable {
    let id: String
    let shortID: String
    let title: String
    let date: String
    let durationSeconds: Int
    let speakers: [String]
    let wordCount: Int
    let hasSummary: Bool
    let hasAudio: Bool
    let revision: String

    enum CodingKeys: String, CodingKey {
        case id, title, date, speakers, revision
        case shortID = "short_id"
        case durationSeconds = "duration_seconds"
        case wordCount = "word_count"
        case hasSummary = "has_summary"
        case hasAudio = "has_audio"
    }

    init(_ meeting: StoredMeeting) {
        let t = meeting.transcript
        id = t.id.uuidString.lowercased()
        shortID = MeetingLibrary.shortID(t.id)
        title = t.title
        date = ISO8601DateFormatter().string(from: t.date)
        durationSeconds = Int(t.durationSeconds.rounded())
        speakers = Rendering.speakers(t)
        wordCount = t.wordCount
        hasSummary = t.summary != nil
        hasAudio = meeting.hasAudio
        self.revision = meeting.revision
    }

    static let schema: Value = Schema.object([
        "id": ["type": "string"],
        "short_id": ["type": "string"],
        "title": ["type": "string"],
        "date": ["type": "string", "description": "ISO 8601"],
        "duration_seconds": ["type": "integer"],
        "speakers": ["type": "array", "items": ["type": "string"]],
        "word_count": ["type": "integer"],
        "has_summary": ["type": "boolean"],
        "has_audio": ["type": "boolean"],
        "revision": ["type": "string"],
    ], required: ["id", "short_id", "title", "date", "duration_seconds", "speakers", "word_count", "has_summary", "has_audio", "revision"])
}

extension CallTool.Result {
    static func text(_ text: String, links: [StoredMeeting] = []) -> CallTool.Result {
        CallTool.Result(content: [.text(text: text, annotations: nil, _meta: nil)] + links.map(Self.link))
    }

    static func structured<Output: Codable>(_ text: String, _ output: Output, links: [StoredMeeting] = []) throws -> CallTool.Result {
        try CallTool.Result(content: [.text(text: text, annotations: nil, _meta: nil)] + links.map(Self.link),
                            structuredContent: output)
    }

    static func failure(_ message: String) -> CallTool.Result {
        CallTool.Result(content: [.text(text: message, annotations: nil, _meta: nil)], isError: true)
    }

    private static func link(_ meeting: StoredMeeting) -> Tool.Content {
        .resourceLink(uri: Rendering.uri(meeting.id), name: MeetingLibrary.shortID(meeting.id),
                      title: meeting.transcript.title, description: Rendering.date(meeting.transcript.date),
                      mimeType: "text/markdown")
    }
}
