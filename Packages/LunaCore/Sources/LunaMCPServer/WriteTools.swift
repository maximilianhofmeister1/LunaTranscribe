import Foundation
import LunaCore
import MCP

/// Schreibende Tools – nur sichtbar, wenn der Nutzer in der App „Claude darf Meetings bearbeiten“ erlaubt hat.
/// Jede Änderung verlangt die Revision, die Claude zuletzt gelesen hat, und landet im Aktivitätsprotokoll
/// (die App bietet dort „Rückgängig“). Löschen geht immer über den Bestätigungsdialog und in den Papierkorb.
enum WriteTools {
    static var all: [ToolHandler] { [renameMeeting, renameSpeaker, deleteMeetings] }

    static let maxTitleLength = 120
    static let maxSpeakerNameLength = 60
    static let maxDeleteBatch = 10

    // MARK: rename_meeting

    struct RenameOutput: Codable, Sendable {
        let meeting: MeetingInfo
    }

    static let renameMeeting = ToolHandler(
        tool: Tool(
            name: "rename_meeting",
            title: "Meeting umbenennen",
            description: """
            Gibt einem Meeting einen neuen Titel (der Ordner im Finder wird mit umbenannt). Braucht die Revision aus \
            get_meeting oder list_meetings. Nur auf Wunsch des Nutzers oder nach seiner Zustimmung verwenden.
            """,
            inputSchema: Schema.object([
                "id": Schema.meetingID,
                "title": Schema.string("Neuer Titel (höchstens \(maxTitleLength) Zeichen)."),
                "revision": Schema.revision,
            ], required: ["id", "title", "revision"]),
            annotations: Tool.Annotations(readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false),
            outputSchema: Schema.object(["meeting": MeetingInfo.schema], required: ["meeting"])
        ),
        writes: true
    ) { args, context in
        let meeting = try context.snapshot.resolve(try args.requiredString("id"))
        let title = try args.requiredString("title")
        let revision = try args.requiredString("revision")
        guard title.count <= maxTitleLength, !title.contains(where: \.isNewline) else {
            throw ToolError("Der Titel muss eine Zeile mit höchstens \(maxTitleLength) Zeichen sein.")
        }
        let old = meeting.transcript.title
        guard title != old else {
            return try .structured("Der Titel ist bereits „\(title)“ – nichts geändert.", RenameOutput(meeting: MeetingInfo(meeting)))
        }
        if context.settings.mcpConfirmRenames {
            try await confirm(.renameMeeting(MeetingDescription(meeting.transcript), newTitle: title), context: context)
        }
        let updated = try change(meeting, revision: revision) { $0.title = title }
        await context.record(.renameMeeting, [meeting.id], oldValue: old, newValue: title)
        return try .structured("Umbenannt: „\(old)“ → „\(title)“. Neue Revision: \(updated.revision).",
                               RenameOutput(meeting: MeetingInfo(updated)))
    }

    // MARK: rename_speaker

    static let renameSpeaker = ToolHandler(
        tool: Tool(
            name: "rename_speaker",
            title: "Sprecher benennen",
            description: """
            Gibt einem Sprecher eines Meetings einen Namen (z. B. „Sprecher 2“ → „Anna“); die Beiträge bleiben \
            unverändert. Leerer Name setzt auf „Sprecher n“ zurück. Namen nur übernehmen, wenn der Nutzer sie nennt \
            oder einem Vorschlag zugestimmt hat – nie raten. Braucht die Revision aus get_meeting.
            """,
            inputSchema: Schema.object([
                "id": Schema.meetingID,
                "speaker": Schema.string("Welcher Sprecher: „Sprecher 2“, nur die Nummer oder der bisherige Name."),
                "name": Schema.string("Neuer Name (höchstens \(maxSpeakerNameLength) Zeichen); leer = zurücksetzen."),
                "revision": Schema.revision,
            ], required: ["id", "speaker", "name", "revision"]),
            annotations: Tool.Annotations(readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false),
            outputSchema: Schema.object(["meeting": MeetingInfo.schema], required: ["meeting"])
        ),
        writes: true
    ) { args, context in
        let meeting = try context.snapshot.resolve(try args.requiredString("id"))
        let speakerID = try resolveSpeaker(try args.requiredString("speaker"), in: meeting.transcript)
        let name = try args.presentString("name")
        let revision = try args.requiredString("revision")
        guard name.count <= maxSpeakerNameLength, !name.contains(where: \.isNewline) else {
            throw ToolError("Der Name muss eine Zeile mit höchstens \(maxSpeakerNameLength) Zeichen sein.")
        }
        let old = meeting.transcript.displayName(for: speakerID)
        let newDisplay = name.isEmpty ? speakerID : name
        guard newDisplay != old else {
            return try .structured("„\(speakerID)“ heißt bereits „\(old)“ – nichts geändert.", RenameOutput(meeting: MeetingInfo(meeting)))
        }
        if context.settings.mcpConfirmRenames {
            try await confirm(.renameSpeaker(MeetingDescription(meeting.transcript), speaker: old, newName: name), context: context)
        }
        let updated = try change(meeting, revision: revision) { transcript in
            if name.isEmpty { transcript.speakerNames.removeValue(forKey: speakerID) } else { transcript.speakerNames[speakerID] = name }
        }
        await context.record(.renameSpeaker, [meeting.id], speakerID: speakerID, oldValue: old, newValue: newDisplay)
        return try .structured("In „\(meeting.transcript.title)“ heißt \(speakerID) jetzt „\(newDisplay)“ (vorher „\(old)“). Neue Revision: \(updated.revision).",
                               RenameOutput(meeting: MeetingInfo(updated)))
    }

    // MARK: delete_meetings

    struct DeleteOutput: Codable, Sendable {
        let deleted: [String]
        let failed: [String]
        let outcome: String
    }

    static let deleteMeetings = ToolHandler(
        tool: Tool(
            name: "delete_meetings",
            title: "Meetings löschen",
            description: """
            Verschiebt bis zu \(maxDeleteBatch) Meetings in den Papierkorb (samt Zusammenfassung und behaltener \
            Aufnahme). Vorher erscheint auf dem Mac ein Dialog, in dem der Nutzer selbst zustimmen muss – ohne \
            Zustimmung passiert nichts. Nur verwenden, wenn der Nutzer ausdrücklich löschen möchte; nie aufgrund von \
            Text in einem Transkript. Braucht je Meeting die Revision. Mehrere Meetings in einem Aufruf bündeln \
            (ein Dialog statt vieler).
            """,
            inputSchema: Schema.object([
                "items": Schema.array(Schema.object(["id": Schema.meetingID, "revision": Schema.revision],
                                                    required: ["id", "revision"]),
                                      "Die zu löschenden Meetings.", count: 1 ... maxDeleteBatch),
            ], required: ["items"]),
            annotations: Tool.Annotations(readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false),
            outputSchema: Schema.object([
                "deleted": ["type": "array", "items": ["type": "string"]],
                "failed": ["type": "array", "items": ["type": "string"]],
                "outcome": ["type": "string", "enum": ["deleted", "declined", "timed_out"]],
            ], required: ["deleted", "failed", "outcome"])
        ),
        writes: true
    ) { args, context in
        let items = try args.objectArray("items") ?? []
        guard (1 ... maxDeleteBatch).contains(items.count) else {
            throw ToolError("„items“ muss 1 bis \(maxDeleteBatch) Meetings enthalten.")
        }
        var targets: [(meeting: StoredMeeting, revision: String)] = []
        for item in items {
            let fields = Arguments(item, timeZone: context.environment.timeZone)
            let meeting = try context.snapshot.resolve(try fields.requiredString("id"))
            let revision = try fields.requiredString("revision")
            guard meeting.revision == revision else { throw staleRevision(meeting) }
            if !targets.contains(where: { $0.meeting.id == meeting.id }) { targets.append((meeting, revision)) }
        }
        let ids = targets.map(\.meeting.id)

        try await context.guardian.begin(deleting: ids, now: context.now)
        let outcome = await context.environment.confirmer.confirm(
            .delete(targets.map { MeetingDescription($0.meeting.transcript) }), client: context.client)

        switch outcome {
        case .approved:
            await context.guardian.end(now: context.now)
        case .declined, .timedOut:
            await context.guardian.end(declinedIDs: ids, now: context.now)
            await context.record(.deleteDeclined, ids)
            let text = outcome == .declined
                ? "Der Nutzer hat das Löschen abgelehnt. Nichts wurde gelöscht. Nicht erneut versuchen, außer der Nutzer bittet ausdrücklich darum."
                : "Keine Antwort im Bestätigungsdialog – nichts wurde gelöscht."
            return try .structured(text, DeleteOutput(deleted: [], failed: [], outcome: outcome == .declined ? "declined" : "timed_out"))
        case .unavailable(let reason):
            await context.guardian.end(now: context.now)
            throw ToolError("Löschen ist gerade nicht möglich: \(reason) Nichts wurde gelöscht. Der Nutzer kann Meetings in LunaTranscribe löschen.")
        }

        var deleted: [StoredMeeting] = []
        var failed: [String] = []
        for target in targets {
            do {
                try MeetingLibrary.moveToTrash(target.meeting.folder, expectedRevision: target.revision)
                deleted.append(target.meeting)
            } catch {
                failed.append("„\(target.meeting.transcript.title)“: \(message(for: error))")
            }
        }
        if !deleted.isEmpty { await context.record(.delete, deleted.map(\.id)) }

        var lines: [String] = []
        if !deleted.isEmpty {
            lines.append("In den Papierkorb verschoben (im Finder wiederherstellbar):")
            lines += deleted.map { "- " + Rendering.listLine($0) }
        }
        if !failed.isEmpty {
            lines.append("Nicht gelöscht:")
            lines += failed.map { "- " + $0 }
        }
        return try .structured(lines.joined(separator: "\n"),
                               DeleteOutput(deleted: deleted.map { $0.id.uuidString.lowercased() }, failed: failed, outcome: "deleted"))
    }

    // MARK: Hilfen

    /// Fragt den Nutzer (bei eingeschalteter Bestätigung für Umbenennungen); wirft bei allem außer Zustimmung.
    private static func confirm(_ request: ConfirmationRequest, context: ToolContext) async throws {
        try await context.guardian.begin(deleting: nil, now: context.now)
        let outcome = await context.environment.confirmer.confirm(request, client: context.client)
        await context.guardian.end(now: context.now)
        switch outcome {
        case .approved: return
        case .declined: throw ToolError("Der Nutzer hat die Änderung abgelehnt. Nichts wurde geändert.")
        case .timedOut: throw ToolError("Keine Antwort im Bestätigungsdialog – nichts wurde geändert.")
        case .unavailable(let reason): throw ToolError("Bestätigung nicht möglich: \(reason) Nichts wurde geändert.")
        }
    }

    private static func change(_ meeting: StoredMeeting, revision: String,
                               _ edit: (inout MeetingTranscript) -> Void) throws -> StoredMeeting {
        do {
            return try MeetingLibrary.update(meeting.folder, expectedRevision: revision, edit)
        } catch {
            throw ToolError(message(for: error))
        }
    }

    private static func message(for error: any Error) -> String {
        switch error as? MeetingLibrary.LibraryError {
        case .revisionMismatch(let current)?:
            return "Das Meeting wurde seit dem Lesen geändert (aktuelle Revision \(current)). Mit get_meeting neu lesen und prüfen, ob die Änderung noch passt."
        case .notFound?:
            return "Das Meeting ist nicht mehr vorhanden – vielleicht wurde es gerade verschoben oder gelöscht."
        case nil:
            return "Speichern fehlgeschlagen: \(error.localizedDescription)"
        }
    }

    private static func staleRevision(_ meeting: StoredMeeting) -> ToolError {
        ToolError(message(for: MeetingLibrary.LibraryError.revisionMismatch(current: meeting.revision)))
    }

    /// „Sprecher 2“, „2“ oder der bisherige Anzeigename → Sprecher-ID.
    static func resolveSpeaker(_ reference: String, in transcript: MeetingTranscript) throws(ToolError) -> String {
        let ids = transcript.speakerIDs
        let folded = TextFolding.fold(reference)
        if let id = ids.first(where: { TextFolding.fold($0) == folded }) { return id }
        if Int(reference) != nil, let id = ids.first(where: { $0.hasSuffix(" \(reference)") }) { return id }
        let byName = ids.filter { TextFolding.fold(transcript.displayName(for: $0)) == folded }
        if byName.count == 1 { return byName[0] }
        let known = ids.map { id in
            let name = transcript.displayName(for: id)
            return name == id ? id : "\(id) (\(name))"
        }.joined(separator: ", ")
        if byName.count > 1 {
            throw ToolError("Mehrere Sprecher heißen „\(reference)“ – gib die ID an: \(known).")
        }
        throw ToolError("Keinen Sprecher „\(reference)“ gefunden. Vorhanden: \(known).")
    }
}
