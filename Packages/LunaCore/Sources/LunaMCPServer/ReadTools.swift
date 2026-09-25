import Foundation
import LunaCore
import MCP

/// Lesende Tools. Annotationen: nur lesend, geschlossene Welt (nur die lokale Bibliothek), wiederholbar.
enum ReadTools {
    static let annotations = Tool.Annotations(readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false)

    static var all: [ToolHandler] { [listMeetings, searchMeetings, getMeeting, processingStatus] }

    // MARK: list_meetings

    struct ListOutput: Codable, Sendable {
        let meetings: [MeetingInfo]
        let total: Int
        let nextCursor: String?

        enum CodingKeys: String, CodingKey {
            case meetings, total
            case nextCursor = "next_cursor"
        }
    }

    static let listMeetings = ToolHandler(
        tool: Tool(
            name: "list_meetings",
            title: "Meetings auflisten",
            description: """
            Listet Meetings aus LunaTranscribe, neueste zuerst: Datum, Titel, Dauer, Sprecher, Wortzahl, ob es eine \
            Zusammenfassung gibt, und die Revision (für Änderungen). Mit from/to nach Zeitraum und mit speaker nach \
            Person filtern. Für Themen oder Inhalte stattdessen search_meetings nutzen.
            """,
            inputSchema: Schema.object([
                "from": Schema.from,
                "to": Schema.to,
                "speaker": Schema.speaker,
                "has_summary": Schema.boolean("Nur Meetings mit (true) oder ohne (false) Zusammenfassung."),
                "limit": Schema.integer("Anzahl (Standard 20).", range: 1 ... 100),
                "cursor": Schema.string("Fortsetzung aus next_cursor einer vorherigen Antwort."),
            ]),
            annotations: annotations,
            outputSchema: Schema.object([
                "meetings": ["type": "array", "items": MeetingInfo.schema],
                "total": ["type": "integer"],
                "next_cursor": ["type": "string"],
            ], required: ["meetings", "total"])
        ),
        writes: false
    ) { args, context in
        let filter = SearchService.Filter(from: try args.dayStart("from"), to: try args.dayEnd("to"),
                                          speaker: try args.string("speaker").map(TextFolding.fold))
        let hasSummary = try args.bool("has_summary")
        let limit = try args.int("limit", default: 20, range: 1 ... 100)
        let offset = try args.int("cursor", default: 0, range: 0 ... Int.max)

        let matching = context.snapshot.meetings.filter { meeting in
            guard SearchService.matches(meeting, filter) else { return false }
            if let hasSummary, (meeting.transcript.summary != nil) != hasSummary { return false }
            return true
        }
        let page = Array(matching.dropFirst(offset).prefix(limit))
        let next = offset + page.count < matching.count ? String(offset + page.count) : nil

        var lines: [String]
        if matching.isEmpty {
            lines = [context.snapshot.meetings.isEmpty
                ? "Die Bibliothek enthält noch keine (freigegebenen) Meetings."
                : "Keine Meetings passen zu den Filtern."]
        } else {
            lines = ["\(matching.count) Meeting(s), neueste zuerst – \(offset + 1) bis \(offset + page.count):"]
            lines += page.map { "- " + Rendering.listLine($0) + " · Rev \($0.revision)" }
            if let next { lines.append("Weitere: list_meetings(cursor: \"\(next)\") mit denselben Filtern.") }
        }
        return try .structured(lines.joined(separator: "\n"),
                               ListOutput(meetings: page.map(MeetingInfo.init), total: matching.count, nextCursor: next))
    }

    // MARK: search_meetings

    struct SearchOutput: Codable, Sendable {
        struct Hit: Codable, Sendable {
            let meeting: MeetingInfo
            let start: Double?
            let end: Double?
            let turnFrom: Int?
            let turnTo: Int?
            let speakers: [String]
            let excerpt: String
            let sources: [String]

            enum CodingKeys: String, CodingKey {
                case meeting, start, end, speakers, excerpt, sources
                case turnFrom = "turn_from"
                case turnTo = "turn_to"
            }
        }

        let query: String
        let mode: String
        let hits: [Hit]
        let note: String?
    }

    static let searchMeetings = ToolHandler(
        tool: Tool(
            name: "search_meetings",
            title: "Meetings durchsuchen",
            description: """
            Durchsucht alle Meetings nach Themen, Aussagen, Namen oder Begriffen – in Beiträgen, Zusammenfassungen, \
            Titeln und Sprechernamen. Liefert Fundstellen mit Zeitmarke, Sprechern und Ausschnitt. Für Themen und \
            Bedeutungen in natürlicher Sprache fragen („Streit über die Preiserhöhung“); für Namen, Zahlen und \
            Fachbegriffe die genauen Wörter nutzen, Phrasen in Anführungszeichen. Danach mit get_meeting gezielt \
            weiterlesen (from_time der Fundstelle).
            """,
            inputSchema: Schema.object([
                "query": Schema.string("Wonach gesucht wird."),
                "mode": Schema.string("auto = Stichwort + inhaltlich (Standard), keyword = nur exakte Wörter, semantic = nur nach Bedeutung.",
                                      oneOf: SearchService.Mode.allCases.map(\.rawValue)),
                "from": Schema.from,
                "to": Schema.to,
                "speaker": Schema.speaker,
                "limit": Schema.integer("Anzahl Fundstellen (Standard 8).", range: 1 ... 30),
            ], required: ["query"]),
            annotations: annotations,
            outputSchema: Schema.object([
                "query": ["type": "string"],
                "mode": ["type": "string"],
                "note": ["type": "string"],
                "hits": ["type": "array", "items": Schema.object([
                    "meeting": MeetingInfo.schema,
                    "start": ["type": "number"],
                    "end": ["type": "number"],
                    "turn_from": ["type": "integer"],
                    "turn_to": ["type": "integer"],
                    "speakers": ["type": "array", "items": ["type": "string"]],
                    "excerpt": ["type": "string"],
                    "sources": ["type": "array", "items": ["type": "string"]],
                ], required: ["meeting", "speakers", "excerpt", "sources"])],
            ], required: ["query", "mode", "hits"])
        ),
        writes: false
    ) { args, context in
        let query = try args.requiredString("query")
        guard let mode = SearchService.Mode(rawValue: try args.string("mode") ?? "auto") else {
            throw ToolError("„mode“ muss auto, keyword oder semantic sein.")
        }
        let filter = SearchService.Filter(from: try args.dayStart("from"), to: try args.dayEnd("to"),
                                          speaker: try args.string("speaker").map(TextFolding.fold))
        let limit = try args.int("limit", default: 8, range: 1 ... 30)

        let result: SearchService.Result
        do {
            result = try await context.search.search(query, mode: mode, filter: filter, limit: limit,
                                                     snapshot: context.snapshot, settings: context.settings)
        } catch {
            throw ToolError("Die Suche ist gerade nicht möglich: \(error.localizedDescription)")
        }
        if mode == .semantic, result.hits.isEmpty, let note = result.semantic.note {
            throw ToolError(note)
        }

        var lines = ["Suche „\(query)“ – \(result.hits.count) Fundstelle(n):"]
        var hits: [SearchOutput.Hit] = []
        var linked: [StoredMeeting] = []
        for (number, hit) in result.hits.enumerated() {
            guard let meeting = context.snapshot.meeting(hit.meetingID) else { continue }
            let t = meeting.transcript
            let sources = hit.sources.sorted().map(\.rawValue)
            let speakers = (hit.chunk?.speakerIDs ?? t.speakerIDs).map(t.displayName(for:))
            let excerpt: String
            var location = ""
            if let chunk = hit.chunk {
                excerpt = hit.snippet ?? Rendering.excerpt(chunk, in: t)
                if chunk.kind == .summary {
                    location = " — Zusammenfassung"
                } else {
                    location = " — [\(MeetingTranscript.clock(chunk.start))–\(MeetingTranscript.clock(chunk.end))] \(speakers.joined(separator: ", "))"
                }
            } else {
                excerpt = "Titel oder Sprechername passt."
            }
            lines.append("")
            lines.append("\(number + 1). \(MeetingLibrary.shortID(t.id)) · „\(t.title)“ · \(Rendering.date(t.date))\(location) · \(sources.joined(separator: " + "))")
            lines.append(excerpt.split(separator: "\n", omittingEmptySubsequences: false).map { "   > " + $0 }.joined(separator: "\n"))
            if let chunk = hit.chunk, chunk.kind == .turns {
                lines.append("   Weiterlesen: get_meeting(id: \"\(MeetingLibrary.shortID(t.id))\", from_time: \"\(MeetingTranscript.clock(chunk.start))\")")
            }
            hits.append(SearchOutput.Hit(meeting: MeetingInfo(meeting), start: hit.chunk?.start, end: hit.chunk?.end,
                                         turnFrom: hit.chunk?.turns.map { $0.lowerBound + 1 },
                                         turnTo: hit.chunk?.turns.map { $0.upperBound + 1 },
                                         speakers: speakers, excerpt: excerpt, sources: sources))
            if !linked.contains(where: { $0.id == meeting.id }) { linked.append(meeting) }
        }
        if result.hits.isEmpty { lines.append("Nichts gefunden. Andere Wörter, eine allgemeinere Formulierung oder mode: semantic probieren.") }
        if let note = result.semantic.note { lines.append(""); lines.append("Hinweis: " + note) }

        await context.record(.search, linked.map(\.id))
        return try .structured(lines.joined(separator: "\n"),
                               SearchOutput(query: query, mode: mode.rawValue, hits: hits, note: result.semantic.note),
                               links: linked)
    }

    // MARK: get_meeting

    static let getMeeting = ToolHandler(
        tool: Tool(
            name: "get_meeting",
            title: "Meeting lesen",
            description: """
            Liest ein Meeting als Markdown: Metadaten, Revision, Sprecherzuordnung, Zusammenfassung und Transkript mit \
            Zeitmarken [mm:ss]. Lange Meetings werden gekürzt – die Antwort nennt dann den offset für die Fortsetzung. \
            Mit speaker, from_time/to_time oder include nur einen Teil lesen.
            """,
            inputSchema: Schema.object([
                "id": Schema.meetingID,
                "include": Schema.array(Schema.string("Teil", oneOf: ["summary", "transcript"]),
                                        "Welche Teile (Standard: beide)."),
                "speaker": Schema.string("Nur Beiträge dieses Sprechers (Name oder „Sprecher 2“)."),
                "from_time": Schema.string("Ab Zeitmarke, z. B. 12:04."),
                "to_time": Schema.string("Bis Zeitmarke, z. B. 20:00."),
                "offset": Schema.integer("Erster Beitrag (1-basiert), für die Fortsetzung.", range: 1 ... 100_000),
                "max_chars": Schema.integer("Längenbegrenzung (Standard 40000).", range: 2_000 ... 150_000),
            ], required: ["id"]),
            annotations: annotations,
            _meta: Metadata(additionalFields: ["anthropic/maxResultSizeChars": 120_000])
        ),
        writes: false
    ) { args, context in
        let meeting = try context.snapshot.resolve(try args.requiredString("id"))
        let include = Set(try args.stringArray("include") ?? ["summary", "transcript"])
        guard include.isSubset(of: ["summary", "transcript"]), !include.isEmpty else {
            throw ToolError("„include“ darf nur summary und/oder transcript enthalten.")
        }
        var options = Rendering.MeetingOptions()
        options.includeSummary = include.contains("summary")
        options.includeTranscript = include.contains("transcript")
        options.speaker = try args.string("speaker").map(TextFolding.fold)
        options.fromTime = try args.time("from_time")
        options.toTime = try args.time("to_time")
        options.offset = try args.int("offset", default: 1, range: 1 ... 100_000)
        options.maxChars = try args.int("max_chars", default: 40_000, range: 2_000 ... 150_000)

        await context.record(.read, [meeting.id])
        return .text(Rendering.meeting(meeting, options: options))
    }

    // MARK: get_processing_status

    struct StatusOutput: Codable, Sendable {
        struct Job: Codable, Sendable {
            let recordedAt: String
            let durationSeconds: Int
            let failure: String?

            enum CodingKeys: String, CodingKey {
                case failure
                case recordedAt = "recorded_at"
                case durationSeconds = "duration_seconds"
            }
        }

        let pending: [Job]
        let latestMeeting: MeetingInfo?
        let index: String

        enum CodingKeys: String, CodingKey {
            case pending, index
            case latestMeeting = "latest_meeting"
        }
    }

    static let processingStatus = ToolHandler(
        tool: Tool(
            name: "get_processing_status",
            title: "Verarbeitungsstand",
            description: """
            Zeigt, ob beendete Meetings noch verarbeitet werden (Sprechererkennung, Transkription) oder fehlgeschlagen \
            sind, welches Meeting zuletzt fertig wurde, und den Stand des Suchindex. Nützlich bei „Ist mein Meeting von \
            eben schon fertig?“.
            """,
            inputSchema: Schema.object([:]),
            annotations: annotations,
            outputSchema: Schema.object([
                "pending": ["type": "array", "items": Schema.object([
                    "recorded_at": ["type": "string"],
                    "duration_seconds": ["type": "integer"],
                    "failure": ["type": "string"],
                ], required: ["recorded_at", "duration_seconds"])],
                "latest_meeting": MeetingInfo.schema,
                "index": ["type": "string"],
            ], required: ["pending", "index"])
        ),
        writes: false
    ) { _, context in
        let text = await statusText(context)
        let jobs = MeetingJob.readAll(in: context.environment.queueFolder)
        let output = StatusOutput(
            pending: jobs.map { .init(recordedAt: ISO8601DateFormatter().string(from: $0.recordedAt),
                                      durationSeconds: Int($0.durationSeconds.rounded()), failure: $0.failure) },
            latestMeeting: context.snapshot.meetings.first.map(MeetingInfo.init),
            index: await context.search.stats(snapshot: context.snapshot, settings: context.settings))
        return try .structured(text, output)
    }

    /// Gemeinsam mit der Ressource `luna://status`.
    static func statusText(_ context: ToolContext) async -> String {
        let jobs = MeetingJob.readAll(in: context.environment.queueFolder)
        var lines: [String] = []
        if jobs.isEmpty {
            lines.append("Keine Meetings in der Verarbeitung.")
        } else {
            lines.append("\(jobs.count) Meeting(s) in der Warteschlange (älteste zuerst):")
            for job in jobs {
                let state = job.failure.map { "fehlgeschlagen: \($0) – in LunaTranscribe erneut versuchen oder verwerfen" }
                    ?? "wartet bzw. wird verarbeitet"
                lines.append("- Aufnahme vom \(Rendering.date(job.recordedAt)), \(MeetingTranscript.clock(job.durationSeconds)) – \(state)")
            }
        }
        if let latest = context.snapshot.meetings.first {
            lines.append("Zuletzt fertig: " + Rendering.listLine(latest))
        }
        if !context.snapshot.libraryReachable {
            lines.append("Achtung: Der Meeting-Ordner \(context.snapshot.libraryFolder.path) ist nicht erreichbar (z. B. externes Laufwerk).")
        }
        lines.append(await context.search.stats(snapshot: context.snapshot, settings: context.settings))
        return lines.joined(separator: "\n")
    }
}
