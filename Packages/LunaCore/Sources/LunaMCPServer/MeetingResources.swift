import Foundation
import LunaCore
import MCP

/// Ressourcen: vom Nutzer gewählter Kontext (Claude Code: `@`-Erwähnung, Claude Desktop: „+“-Menü).
enum MeetingResources {
    static let overviewURI = "luna://meetings"
    static let vocabularyURI = "luna://vocabulary"
    static let statusURI = "luna://status"
    static let listedMeetings = 50

    static func list(_ context: ToolContext) -> ListResources.Result {
        var resources = [
            Resource(name: "meetings", uri: overviewURI, title: "Meeting-Übersicht",
                     description: "Die letzten \(listedMeetings) Meetings mit Datum, Dauer und Sprechern.", mimeType: "text/markdown"),
            Resource(name: "vocabulary", uri: vocabularyURI, title: "Eigenes Wörterbuch",
                     description: "Richtige Schreibweisen von Namen und Fachbegriffen.", mimeType: "text/markdown"),
            Resource(name: "status", uri: statusURI, title: "Verarbeitungsstand",
                     description: "Wartende Meetings und Stand des Suchindex.", mimeType: "text/markdown"),
        ]
        let iso = ISO8601DateFormatter()
        for meeting in context.snapshot.meetings.prefix(listedMeetings) {
            let t = meeting.transcript
            let modified = (try? meeting.folder.appending(path: MeetingLibrary.transcriptFileName)
                .resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? t.date
            resources.append(Resource(
                name: MeetingLibrary.shortID(t.id),
                uri: Rendering.uri(t.id),
                title: "\(t.title) · \(Rendering.date(t.date))",
                description: "\(MeetingTranscript.clock(t.durationSeconds)) · \(Rendering.speakers(t).joined(separator: ", "))",
                mimeType: "text/markdown",
                annotations: Resource.Annotations(lastModified: iso.string(from: modified))))
        }
        return ListResources.Result(resources: resources)
    }

    static let templates: [Resource.Template] = [
        Resource.Template(uriTemplate: "luna://meetings/{id}", name: "meeting", title: "Meeting",
                          description: "Ein Meeting als Markdown: Zusammenfassung und Transkript.", mimeType: "text/markdown"),
        Resource.Template(uriTemplate: "luna://meetings/{id}/summary", name: "meeting-summary", title: "Zusammenfassung",
                          description: "Nur die Zusammenfassung eines Meetings.", mimeType: "text/markdown"),
        Resource.Template(uriTemplate: "luna://meetings/{id}/transcript.json", name: "meeting-json", title: "meeting.json",
                          description: "Die Originaldatei – für Auswertungen per Code.", mimeType: "application/json"),
    ]

    static func read(_ uri: String, _ context: ToolContext) async throws -> ReadResource.Result {
        switch uri {
        case overviewURI:
            let lines = context.snapshot.meetings.prefix(listedMeetings).map { "- " + Rendering.listLine($0) }
            let text = lines.isEmpty ? "Noch keine (freigegebenen) Meetings." : "# Meetings (neueste zuerst)\n\n" + lines.joined(separator: "\n")
            return .init(contents: [.text(text, uri: uri, mimeType: "text/markdown")])
        case vocabularyURI:
            return .init(contents: [.text(vocabularyText(context.settings), uri: uri, mimeType: "text/markdown")])
        case statusURI:
            return .init(contents: [.text(await ReadTools.statusText(context), uri: uri, mimeType: "text/markdown")])
        default:
            break
        }

        let prefix = overviewURI + "/"
        guard uri.hasPrefix(prefix) else { throw notFound(uri) }
        let parts = uri.dropFirst(prefix.count).split(separator: "/", maxSplits: 1).map(String.init)
        guard let reference = parts.first else { throw notFound(uri) }
        let meeting: StoredMeeting
        do {
            meeting = try context.snapshot.resolve(reference)
        } catch {
            throw MCPError.serverError(code: -32002, message: error.message)
        }
        await context.record(.read, [meeting.id])

        switch parts.count == 2 ? parts[1] : "" {
        case "":
            return .init(contents: [.text(Rendering.meeting(meeting, options: .init(maxChars: 500_000)), uri: uri, mimeType: "text/markdown")])
        case "summary":
            var options = Rendering.MeetingOptions()
            options.includeTranscript = false
            return .init(contents: [.text(Rendering.meeting(meeting, options: options), uri: uri, mimeType: "text/markdown")])
        case "transcript.json":
            let data = try Data(contentsOf: meeting.folder.appending(path: MeetingLibrary.transcriptFileName))
            return .init(contents: [.text(String(decoding: data, as: UTF8.self), uri: uri, mimeType: "application/json")])
        default:
            throw notFound(uri)
        }
    }

    /// Vorschläge für `{id}`: passende Kurz-IDs nach ID-Anfang, Titel oder Datum („25.09“).
    static func complete(_ value: String, snapshot: MeetingCatalog.Snapshot) -> [String] {
        let needle = TextFolding.fold(value)
        return snapshot.meetings.filter { meeting in
            guard !needle.isEmpty else { return true }
            let t = meeting.transcript
            return MeetingLibrary.shortID(t.id).hasPrefix(needle)
                || TextFolding.fold(t.title).contains(needle)
                || Rendering.date(t.date).contains(needle)
        }
        .prefix(20)
        .map { MeetingLibrary.shortID($0.id) }
    }

    static func vocabularyText(_ settings: SharedSettings) -> String {
        let entries = settings.vocabularyEntries.filter { !$0.term.trimmingCharacters(in: .whitespaces).isEmpty }
        guard !entries.isEmpty else { return "Das Wörterbuch ist leer." }
        var lines = ["# Eigenes Wörterbuch", "",
                     "Richtige Schreibweisen – bitte in Antworten so verwenden. Transkripte können noch die Fehlschreibungen enthalten.", ""]
        if !settings.vocabularyEnabled { lines.append("_Das Wörterbuch ist in LunaTranscribe gerade ausgeschaltet._\n") }
        for entry in entries {
            let aliases = entry.aliases.filter { !$0.isEmpty }
            lines.append("- **\(entry.term)**" + (aliases.isEmpty ? "" : " (falsch erkannt als: \(aliases.joined(separator: ", ")))"))
        }
        return lines.joined(separator: "\n")
    }

    private static func notFound(_ uri: String) -> MCPError {
        .serverError(code: -32002, message: "Unbekannte Ressource \(uri). Verfügbar: \(overviewURI), \(overviewURI)/{id}, …/summary, …/transcript.json, \(vocabularyURI), \(statusURI).")
    }
}
