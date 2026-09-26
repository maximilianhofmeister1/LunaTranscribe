import Foundation
import LunaCore
import MCP

/// Prompts: wiederverwendbare Arbeitsaufträge (Claude Code: Slash-Befehle, Claude Desktop: „+“-Menü).
/// Wo es um ein Meeting geht, wird es als Ressource eingebettet; übergreifende Prompts weisen Claude an,
/// die Tools zu nutzen.
enum MeetingPrompts {
    static let idArgument = Prompt.Argument(name: "id", title: "Meeting", description: "Meeting-ID (mindestens 6 Zeichen)", required: true)

    static let all: [Prompt] = [
        Prompt(name: "meeting_summary", title: "Meeting zusammenfassen",
               description: "Fasst ein Meeting im Format von LunaTranscribe zusammen (Kurzfassung, Themen, Entscheidungen, Aufgaben, offene Fragen).",
               arguments: [idArgument]),
        Prompt(name: "weekly_review", title: "Wochenrückblick",
               description: "Entscheidungen, offene Aufgaben je Person und offene Fragen aller Meetings einer Woche.",
               arguments: [Prompt.Argument(name: "week", title: "Woche",
                                           description: "ISO-Woche wie 2026-W39, „letzte“ oder leer für diese Woche", required: false)]),
        Prompt(name: "action_items", title: "Aufgaben sammeln",
               description: "Aufgaben mit Verantwortlichen und Fristen aus Meetings, jeweils mit Beleg.",
               arguments: [
                   Prompt.Argument(name: "person", title: "Person", description: "Nur Aufgaben dieser Person", required: false),
                   Prompt.Argument(name: "from", title: "Von", description: "JJJJ-MM-TT", required: false),
                   Prompt.Argument(name: "to", title: "Bis", description: "JJJJ-MM-TT", required: false),
               ]),
        Prompt(name: "follow_up_email", title: "Follow-up-Mail",
               description: "Entwurf einer Follow-up-Mail zu einem Meeting.",
               arguments: [idArgument, Prompt.Argument(name: "recipient", title: "Empfänger", description: "An wen", required: false)]),
        Prompt(name: "find_decision", title: "Entscheidung finden",
               description: "Wann und von wem wurde etwas entschieden? Mit Belegstellen.",
               arguments: [Prompt.Argument(name: "topic", title: "Thema", description: "Worum ging es", required: true)]),
        Prompt(name: "name_speakers", title: "Sprecher benennen",
               description: "Schlägt Namen für „Sprecher n“ aus dem Gesprächsinhalt vor und benennt sie erst nach deiner Zustimmung.",
               arguments: [idArgument]),
    ]

    static func get(_ name: String, arguments: [String: String]?, context: ToolContext) async throws -> GetPrompt.Result {
        let args = Arguments(strings: arguments, timeZone: context.environment.timeZone)
        switch name {
        case "meeting_summary":
            let meeting = try context.snapshot.resolve(try args.requiredString("id"))
            let instructions = SummaryPrompt.instructions(template: context.settings.summaryPromptTemplate,
                                                          language: meeting.transcript.language)
            return try await embedding(meeting, context: context, description: "Zusammenfassung von „\(meeting.transcript.title)“",
                                       instructions: instructions)

        case "weekly_review":
            let (start, end) = try week(try args.string("week"), context: context)
            let lastDay = end.addingTimeInterval(-1)
            let tz = context.environment.timeZone
            return .init(description: "Wochenrückblick", messages: [.user(.text(text: """
                Erstelle einen Wochenrückblick über meine Meetings vom \(day(start, tz)) bis \(day(lastDay, tz)).
                Hole die Meetings mit list_meetings(from: "\(iso(start, tz))", to: "\(iso(lastDay, tz))") und lies jedes mit \
                get_meeting (die Zusammenfassung reicht, wo es eine gibt; sonst das Transkript).

                Gliederung:
                ## Überblick – ein Satz je Meeting
                ## Entscheidungen – mit Meeting und Zeitmarke als Beleg
                ## Offene Aufgaben je Person – „- [ ] Person: Aufgabe (Frist)“, mit Beleg
                ## Offene Fragen und Risiken

                Erfinde nichts; wo Sprecher nur „Sprecher n“ heißen, übernimm das so.
                """))])

        case "action_items":
            let person = try args.string("person")
            let range = [try args.string("from").map { "from: \"\($0)\"" }, try args.string("to").map { "to: \"\($0)\"" }]
                .compactMap { $0 }.joined(separator: ", ")
            return .init(description: "Aufgaben", messages: [.user(.text(text: """
                Sammle die Aufgaben aus meinen Meetings\(person.map { " für \($0)" } ?? "")\(range.isEmpty ? " der letzten zwei Wochen" : "").
                Nutze list_meetings(\(range)) und search_meetings (z. B. „Aufgabe“, „bis Freitag“, „kümmert sich“) und lies die \
                Fundstellen mit get_meeting nach.
                Format: „- [ ] Person: Aufgabe (Frist) – Meeting, [mm:ss]“. Nur, was wirklich vereinbart wurde; Unklares separat \
                unter „Unklar“.
                """))])

        case "follow_up_email":
            let meeting = try context.snapshot.resolve(try args.requiredString("id"))
            let recipient = try args.string("recipient")
            return try await embedding(meeting, context: context, description: "Follow-up-Mail", instructions: """
                Schreibe den Entwurf einer Follow-up-Mail\(recipient.map { " an \($0)" } ?? "") zu diesem Meeting: kurzer Dank, \
                Zusammenfassung in zwei, drei Sätzen, Entscheidungen, nächste Schritte mit Verantwortlichen und Fristen. \
                Sprache und Ton wie im Meeting (Du/Sie). Nichts erfinden.
                """)

        case "find_decision":
            let topic = try args.requiredString("topic")
            return .init(description: "Entscheidung finden", messages: [.user(.text(text: """
                Finde heraus, wann und von wem zu „\(topic)“ entschieden wurde. Suche mit search_meetings (erst inhaltlich, dann \
                mit genauen Begriffen), lies die Fundstellen mit get_meeting nach und antworte mit Datum, Meeting, Person und \
                wörtlichem Zitat samt Zeitmarke. Gab es mehrere Entscheidungen, zeige die Entwicklung. Findest du nichts \
                Eindeutiges, sag das.
                """))])

        case "name_speakers":
            let meeting = try context.snapshot.resolve(try args.requiredString("id"))
            return try await embedding(meeting, context: context, description: "Sprecher benennen", instructions: """
                Schlage für die Sprecher dieses Meetings Namen vor – nur, wo der Gesprächsinhalt sie belegt (Anrede, \
                Vorstellung, „danke, Anna“). Nenne zu jedem Vorschlag die Belegstelle mit Zeitmarke und wie sicher du bist. \
                Frag mich dann, welche du übernehmen sollst, und benenne erst nach meiner Zustimmung mit rename_speaker \
                (Revision: \(meeting.revision)). Keine Namen raten.
                """)

        default:
            throw MCPError.invalidParams("Unbekannter Prompt „\(name)“.")
        }
    }

    // MARK: Hilfen

    private static func embedding(_ meeting: StoredMeeting, context: ToolContext, description: String,
                                  instructions: String) async throws -> GetPrompt.Result {
        await context.record(.read, [meeting.id])
        let markdown = Rendering.meeting(meeting, options: .init(maxChars: 500_000))
        return .init(description: description, messages: [
            .user(.resource(resource: .text(markdown, uri: Rendering.uri(meeting.id), mimeType: "text/markdown"))),
            .user(.text(text: instructions)),
        ])
    }

    /// Montag 0:00 bis Montag 0:00 der gewünschten ISO-Woche in der Zeitzone des Nutzers.
    static func week(_ text: String?, context: ToolContext) throws -> (Date, Date) {
        var calendar = Calendar(identifier: .iso8601)
        calendar.timeZone = context.environment.timeZone
        let now = context.now
        var reference = now
        if let text, !text.isEmpty {
            let lowered = text.lowercased()
            if lowered.hasPrefix("letzt") || lowered == "last" {
                reference = calendar.date(byAdding: .weekOfYear, value: -1, to: now) ?? now
            } else if lowered.hasPrefix("dies") || lowered == "this" {
                reference = now
            } else {
                let parts = lowered.split(separator: "-")
                guard parts.count == 2, let year = Int(parts[0]), parts[1].hasPrefix("w"), let number = Int(parts[1].dropFirst()),
                      let date = calendar.date(from: DateComponents(weekday: 2, weekOfYear: number, yearForWeekOfYear: year)) else {
                    throw ToolError("„week“ muss wie 2026-W39 aussehen (oder „letzte“ / leer).")
                }
                reference = date
            }
        }
        guard let interval = calendar.dateInterval(of: .weekOfYear, for: reference) else {
            throw ToolError("Woche konnte nicht bestimmt werden.")
        }
        return (interval.start, interval.end)
    }

    private static func day(_ date: Date, _ timeZone: TimeZone) -> String {
        let formatter = DateFormatter()
        formatter.timeZone = timeZone
        formatter.locale = Locale(identifier: "de_DE")
        formatter.dateFormat = "dd.MM.yyyy"
        return formatter.string(from: date)
    }

    private static func iso(_ date: Date, _ timeZone: TimeZone) -> String {
        let formatter = DateFormatter()
        formatter.timeZone = timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from: date)
    }
}
