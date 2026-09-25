import Foundation
import Testing
@testable import LunaCore

@Suite("Meeting-Bibliothek")
struct MeetingLibraryTests {
    @Test("Liest das bestehende v0.2-Format, ohne neues Feld isConfidential")
    func decodesExistingFormat() throws {
        let transcript = try JSONCoding.decoder().decode(MeetingTranscript.self, from: try Support.fixtureData("meeting-v0.2.json"))
        #expect(transcript.title == "Planung Q4")
        #expect(transcript.turns.count == 2)
        #expect(transcript.displayName(for: "Sprecher 1") == "Anna")
        #expect(transcript.displayName(for: "Sprecher 2") == "Sprecher 2")
        #expect(transcript.isConfidential == nil)
        #expect(!transcript.confidential)
    }

    @Test("Schreiben, Lesen, Umbenennen mit Ordnerwechsel")
    func writeReadRename() throws {
        let library = try Support.temporaryFolder()
        let meeting = Support.meeting(lines: [("Sprecher 1", "Hallo zusammen.")], summary: "## Kurzfassung\nKurz.")
        let folder = try MeetingLibrary.write(meeting, into: library, existing: nil)
        #expect(folder.lastPathComponent.hasSuffix(" · \(MeetingLibrary.shortID(meeting.id))"))
        #expect(FileManager.default.fileExists(atPath: folder.appending(path: "summary.md").path))

        let stored = try MeetingLibrary.read(folder: folder)
        #expect(stored.transcript == meeting)

        let renamed = try MeetingLibrary.update(folder, expectedRevision: stored.revision) { $0.title = "Neuer Titel" }
        #expect(renamed.transcript.title == "Neuer Titel")
        #expect(renamed.folder.lastPathComponent.contains("Neuer Titel"))
        #expect(!FileManager.default.fileExists(atPath: folder.path))
        #expect(renamed.revision != stored.revision)
    }

    @Test("Veraltete Revision wird abgelehnt")
    func rejectsStaleRevision() throws {
        let library = try Support.temporaryFolder()
        let folder = try MeetingLibrary.write(Support.meeting(lines: [("Sprecher 1", "Text")]), into: library, existing: nil)
        let stale = try MeetingLibrary.read(folder: folder).revision
        let current = try MeetingLibrary.update(folder, expectedRevision: stale) { $0.speakerNames["Sprecher 1"] = "Anna" }

        #expect(throws: MeetingLibrary.LibraryError.revisionMismatch(current: current.revision)) {
            try MeetingLibrary.update(current.folder, expectedRevision: stale) { $0.title = "Egal" }
        }
        #expect(throws: MeetingLibrary.LibraryError.revisionMismatch(current: current.revision)) {
            try MeetingLibrary.moveToTrash(current.folder, expectedRevision: stale)
        }
    }

    @Test("Papierkorb entfernt den Ordner aus der Bibliothek")
    func trash() throws {
        let library = try Support.temporaryFolder()
        let folder = try MeetingLibrary.write(Support.meeting(lines: [("Sprecher 1", "Text")]), into: library, existing: nil)
        let revision = try MeetingLibrary.read(folder: folder).revision
        try MeetingLibrary.moveToTrash(folder, expectedRevision: revision)
        #expect(MeetingLibrary.scan(library).isEmpty)
        #expect(throws: MeetingLibrary.LibraryError.notFound) { try MeetingLibrary.moveToTrash(folder) }
    }

    @Test("Symbolische Links werden nicht als Meetings gelesen")
    func skipsSymlinks() throws {
        let outside = try Support.temporaryFolder()
        let library = try Support.temporaryFolder()
        let folder = try MeetingLibrary.write(Support.meeting(lines: [("Sprecher 1", "Text")]), into: outside, existing: nil)
        try FileManager.default.createSymbolicLink(at: library.appending(path: "link"), withDestinationURL: folder)
        #expect(MeetingLibrary.scan(library).isEmpty)
    }

    @Test("Zusammenführen: lokale Änderungen gewinnen, sonst der Stand der Platte")
    func threeWayMerge() {
        var base = Support.meeting(lines: [("Sprecher 1", "Hallo"), ("Sprecher 2", "Tschüss")])
        base.speakerNames = ["Sprecher 1": "Anna"]

        var disk = base          // von luna-mcp geändert
        disk.title = "Von Claude"
        disk.speakerNames["Sprecher 2"] = "Tom"

        var local = base         // vom Nutzer in der App geändert
        local.turns[0].text = "Hallo zusammen"
        local.speakerNames["Sprecher 1"] = "Anne"

        let merged = MeetingLibrary.merge(local: local, base: base, disk: disk)
        #expect(merged.title == "Von Claude")
        #expect(merged.turns[0].text == "Hallo zusammen")
        #expect(merged.speakerNames == ["Sprecher 1": "Anne", "Sprecher 2": "Tom"])
    }
}

@Suite("Aktivitätsprotokoll")
struct ActivityLogTests {
    @Test("Anhängen, lesen (neueste zuerst), aufräumen")
    func appendReadCompact() throws {
        let log = ActivityLog(url: try Support.temporaryFolder().appending(path: "log.jsonl"))
        let id = UUID()
        try log.append(ActivityEntry(date: Date(timeIntervalSinceNow: -40 * 24 * 3600), client: "test", action: .read, meetingIDs: [id]))
        try log.append(ActivityEntry(client: "test", action: .renameMeeting, meetingIDs: [id], oldValue: "Alt", newValue: "Neu"))
        #expect(log.entries().map(\.action) == [.renameMeeting, .read])
        #expect(log.entries().first?.isChange == true)

        try log.compact()
        #expect(log.entries().map(\.action) == [.renameMeeting])
    }
}
