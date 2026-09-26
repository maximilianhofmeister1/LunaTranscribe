import Foundation
import LunaCore

/// Textdarstellung für Claude: kompaktes Markdown statt `meeting.json` (keine UUIDs pro Beitrag, keine
/// Einrückung), Zeitmarken im Format der App, Sprechernamen aufgelöst.
enum Rendering {
    static func date(_ date: Date) -> String {
        MeetingTranscript.Formatters.headerDate.string(from: date)
    }

    static func speakers(_ transcript: MeetingTranscript) -> [String] {
        transcript.speakerIDs.map(transcript.displayName(for:))
    }

    /// „a1b2c3 · 25.09.2026, 14:03 · „Planung Q4“ · 47:12 · Anna, Sprecher 2 · 6210 Wörter · Zusammenfassung ✓“
    static func listLine(_ meeting: StoredMeeting) -> String {
        let t = meeting.transcript
        var parts = [
            MeetingLibrary.shortID(t.id),
            date(t.date),
            "„\(t.title)“",
            MeetingTranscript.clock(t.durationSeconds),
        ]
        let names = speakers(t)
        if !names.isEmpty { parts.append(names.joined(separator: ", ")) }
        parts.append("\(t.wordCount) Wörter")
        parts.append(t.summary == nil ? "ohne Zusammenfassung" : "Zusammenfassung ✓")
        return parts.joined(separator: " · ")
    }

    /// Kopf für `get_meeting` und Ressourcen: Titel, Metadaten, Revision, Sprecherzuordnung.
    static func header(_ meeting: StoredMeeting) -> [String] {
        let t = meeting.transcript
        var lines = ["# \(t.title)"]
        lines.append("_\(t.metadataLine) · ID \(MeetingLibrary.shortID(t.id)) · Revision \(meeting.revision)_")
        let mapping = t.speakerIDs.map { id in
            let name = t.displayName(for: id)
            return name == id ? id : "\(id) = \(name)"
        }
        if !mapping.isEmpty { lines.append("Sprecher: " + mapping.joined(separator: " · ")) }
        return lines
    }

    static func turnLine(_ turn: MeetingTranscript.Turn, in transcript: MeetingTranscript) -> String {
        "[\(MeetingTranscript.clock(turn.start))] \(transcript.displayName(for: turn.speakerID)): \(turn.text)"
    }

    struct MeetingOptions {
        var includeSummary = true
        var includeTranscript = true
        /// Gefalteter Sprechername oder -ID.
        var speaker: String?
        var fromTime: TimeInterval?
        var toTime: TimeInterval?
        /// Erster Beitrag, 1-basiert (wie in der Ausgabe gezählt).
        var offset = 1
        var maxChars = 40_000
    }

    /// Ganzes Meeting (oder Ausschnitt) als Markdown; kürzt bei `maxChars` mit Fortsetzungshinweis.
    static func meeting(_ meeting: StoredMeeting, options: MeetingOptions = MeetingOptions()) -> String {
        let t = meeting.transcript
        var lines = header(meeting)

        if options.includeSummary {
            lines.append("")
            lines.append("## Zusammenfassung")
            lines.append("")
            if let summary = t.summary {
                lines.append(MeetingTranscript.demoteHeadings(summary.markdown))
            } else {
                lines.append("_Noch keine Zusammenfassung. Der Nutzer kann in LunaTranscribe auf „Zusammenfassen“ klicken, oder du fasst mit dem Prompt meeting_summary zusammen._")
            }
        }
        guard options.includeTranscript else { return lines.joined(separator: "\n") }

        let speakerIDs = options.speaker.map { SearchService.speakerIDs(matching: $0, in: t) }
        let selected = t.turns.indices.filter { index in
            let turn = t.turns[index]
            if index + 1 < options.offset { return false }
            if let from = options.fromTime, turn.end < from { return false }
            if let to = options.toTime, turn.start > to { return false }
            if let speakerIDs, !speakerIDs.contains(turn.speakerID) { return false }
            return true
        }

        var budget = options.maxChars - lines.reduce(0) { $0 + $1.count + 1 }
        var body: [String] = []
        var cutAt: Int?
        for index in selected {
            let line = turnLine(t.turns[index], in: t)
            // Mindestens ein Beitrag, sonst käme man mit der Fortsetzung nie weiter.
            if !body.isEmpty, budget - line.count - 1 < 0 {
                cutAt = index
                break
            }
            body.append(line)
            budget -= line.count + 1
        }

        lines.append("")
        if selected.isEmpty {
            lines.append("## Transkript")
            lines.append("")
            lines.append(t.turns.isEmpty ? "_Keine Beiträge erkannt._" : "_Keine Beiträge passen zu den Filtern._")
            return lines.joined(separator: "\n")
        }
        let first = selected[0] + 1
        let last = (cutAt.map { selected[selected.firstIndex(of: $0)! - 1] } ?? selected.last!) + 1
        let filtered = options.speaker != nil || options.fromTime != nil || options.toTime != nil
        lines.append("## Transkript (Beiträge \(first)–\(last) von \(t.turns.count)\(filtered ? ", gefiltert" : ""))")
        lines.append("")
        lines += body
        if let cutAt {
            lines.append("")
            lines.append("_Gekürzt bei \(options.maxChars) Zeichen. Fortsetzung: get_meeting(id: \"\(MeetingLibrary.shortID(t.id))\", offset: \(cutAt + 1)) – mit denselben Filtern._")
        }
        return lines.joined(separator: "\n")
    }

    /// Beiträge eines Abschnitts mit Namen – für inhaltliche Treffer, die keinen Stichwort-Ausschnitt haben.
    static func excerpt(_ record: SearchIndex.ChunkRecord, in transcript: MeetingTranscript, maxChars: Int = 420) -> String {
        guard record.kind == .turns, let range = record.turns, range.upperBound < transcript.turns.count else {
            return String(record.text.prefix(maxChars))
        }
        let text = range.map { turnLine(transcript.turns[$0], in: transcript) }.joined(separator: "\n")
        return text.count > maxChars ? String(text.prefix(maxChars)) + " …" : text
    }

    static func uri(_ id: UUID) -> String { "luna://meetings/\(id.uuidString.lowercased())" }
}
