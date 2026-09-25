import Foundation

/// Ein Meeting, wie es auf der Platte liegt: Inhalt, Ordner und die Revision der `meeting.json`.
public struct StoredMeeting: Identifiable, Sendable {
    public let transcript: MeetingTranscript
    public let folder: URL
    /// Merkmal des gelesenen Dateistands; Schreibvorgänge von außen verlangen sie (optimistische Sperre).
    public let revision: String
    public let hasAudio: Bool
    /// Liegt im Rettungsordner, weil der Meeting-Ordner nicht erreichbar war.
    public let isUnsaved: Bool

    public var id: UUID { transcript.id }
}

/// Die Meeting-Bibliothek auf der Platte: ein Unterordner pro Meeting im frei wählbaren Meeting-Ordner.
///
///     <Meeting-Ordner>/2026-09-25 14-03 Planung Q4 · a1b2c3/
///         meeting.json   – Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit)
///         summary.md     – Zusammenfassung als Markdown (für Finder, Quick Look, Claude CLI)
///         audio.wav      – nur mit „Aufnahme behalten“
///
/// App (`MeetingStore`) und luna-mcp lesen und schreiben ausschließlich über diese Funktionen. Beide
/// Prozesse schreiben atomar; Konflikte fangen Revisionen (luna-mcp) und `merge` (App) ab.
public enum MeetingLibrary {
    public static let transcriptFileName = "meeting.json"
    public static let summaryFileName = "summary.md"
    public static let audioFileName = "audio.wav"

    public enum LibraryError: LocalizedError, Equatable {
        case notFound
        case revisionMismatch(current: String)

        public var errorDescription: String? {
            switch self {
            case .notFound:
                return "Das Meeting wurde nicht gefunden – vielleicht wurde es im Finder verschoben oder gelöscht."
            case .revisionMismatch:
                return "Das Meeting wurde inzwischen geändert."
            }
        }
    }

    // MARK: Lesen

    /// Unterordner mit einer `meeting.json`. Symbolische Links werden übersprungen, damit nichts außerhalb
    /// des Meeting-Ordners gelesen oder verändert wird.
    public static func meetingFolders(in parent: URL) -> [URL] {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: parent, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey],
                                                       options: [.skipsHiddenFiles]) else { return [] }
        return items.filter { url in
            let values = try? url.resourceValues(forKeys: [.isSymbolicLinkKey])
            return values?.isSymbolicLink != true && fm.fileExists(atPath: url.appending(path: transcriptFileName).path)
        }
    }

    public static func readTranscript(in folder: URL) throws -> MeetingTranscript {
        try read(folder: folder).transcript
    }

    public static func read(folder: URL, unsaved: Bool = false) throws -> StoredMeeting {
        let data = try Data(contentsOf: folder.appending(path: transcriptFileName))
        let transcript = try JSONCoding.decoder().decode(MeetingTranscript.self, from: data)
        let hasAudio = FileManager.default.fileExists(atPath: folder.appending(path: audioFileName).path)
        return StoredMeeting(transcript: transcript, folder: folder, revision: ContentHash.hex(data),
                             hasAudio: hasAudio, isUnsaved: unsaved)
    }

    /// Alle lesbaren Meetings eines Ordners; unlesbare meldet `onError`, ohne den Rest aufzuhalten.
    public static func scan(_ parent: URL, unsaved: Bool = false,
                            onError: (URL, any Error) -> Void = { _, _ in }) -> [StoredMeeting] {
        meetingFolders(in: parent).compactMap { folder in
            do {
                return try read(folder: folder, unsaved: unsaved)
            } catch {
                onError(folder, error)
                return nil
            }
        }
    }

    // MARK: Schreiben

    /// Schreibt `meeting.json` (atomar) und `summary.md` und gibt den (ggf. umbenannten) Ordner zurück.
    /// Mit `existing` wird der vorhandene Ordner aktualisiert und bei geändertem Titel umbenannt.
    @discardableResult
    public static func write(_ transcript: MeetingTranscript, into parent: URL, existing: URL?) throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: parent, withIntermediateDirectories: true)
        let wantedName = folderName(for: transcript)

        var folder: URL
        if let existing {
            guard fm.fileExists(atPath: existing.appending(path: transcriptFileName).path) else {
                throw LibraryError.notFound
            }
            folder = existing
            if existing.lastPathComponent != wantedName {
                let renamed = uniqueURL(in: parent, name: wantedName)
                try fm.moveItem(at: existing, to: renamed)
                folder = renamed
            }
        } else {
            folder = uniqueURL(in: parent, name: wantedName)
            try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        }

        let data = try JSONCoding.encoder().encode(transcript)
        try data.write(to: folder.appending(path: transcriptFileName), options: .atomic)

        let summaryURL = folder.appending(path: summaryFileName)
        if let summary = transcript.summary {
            let markdown = "# \(transcript.title) – Zusammenfassung\n\n\(summary.markdown)\n"
            try markdown.write(to: summaryURL, atomically: true, encoding: .utf8)
        } else if fm.fileExists(atPath: summaryURL.path) {
            try fm.removeItem(at: summaryURL)
        }
        return folder
    }

    /// Schreibt eine Änderung nur, wenn die Datei noch den Stand `expectedRevision` hat. Für Schreiber
    /// außerhalb der App (luna-mcp), die nicht mitbekommen, was die App gerade tut.
    @discardableResult
    public static func update(_ folder: URL, expectedRevision: String,
                              _ change: (inout MeetingTranscript) throws -> Void) throws -> StoredMeeting {
        let current = try readExisting(folder)
        guard current.revision == expectedRevision else {
            throw LibraryError.revisionMismatch(current: current.revision)
        }
        var transcript = current.transcript
        try change(&transcript)
        let newFolder = try write(transcript, into: folder.deletingLastPathComponent(), existing: folder)
        return try read(folder: newFolder)
    }

    /// Verschiebt ein Meeting in den Papierkorb (im Finder wiederherstellbar) – nie endgültig löschen.
    public static func moveToTrash(_ folder: URL, expectedRevision: String? = nil) throws {
        let current = try readExisting(folder)
        if let expectedRevision, current.revision != expectedRevision {
            throw LibraryError.revisionMismatch(current: current.revision)
        }
        #if os(macOS)
        try FileManager.default.trashItem(at: folder, resultingItemURL: nil)
        #else
        // Außerhalb von macOS (Tests unter Linux): Papierkorb nach freedesktop.org, ohne .trashinfo.
        let trash = URL.homeDirectory.appending(path: ".local/share/Trash/files", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: trash, withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: folder, to: uniqueURL(in: trash, name: folder.lastPathComponent))
        #endif
    }

    // MARK: Zusammenführen

    /// Dreiwege-Zusammenführung für einen Speichervorgang der App, wenn die Datei seit dem Laden von
    /// außen geändert wurde (luna-mcp: Titel, Sprechernamen). Was der Nutzer lokal geändert hat, gewinnt;
    /// alles andere übernimmt den Stand der Platte.
    public static func merge(local: MeetingTranscript, base: MeetingTranscript, disk: MeetingTranscript) -> MeetingTranscript {
        var merged = local
        if local.title == base.title { merged.title = disk.title }
        if local.summary == base.summary { merged.summary = disk.summary }
        if local.isConfidential == base.isConfidential { merged.isConfidential = disk.isConfidential }
        if local.turns == base.turns { merged.turns = disk.turns }

        var names = disk.speakerNames
        for key in Set(local.speakerNames.keys).union(base.speakerNames.keys)
        where local.speakerNames[key] != base.speakerNames[key] {
            names[key] = local.speakerNames[key]  // lokale Änderung gewinnt, auch ein Zurücksetzen
        }
        merged.speakerNames = names
        return merged
    }

    // MARK: Namen

    /// „2026-09-25 14-03 Planung Q4 · a1b2c3“ – Datum zuerst, damit der Finder chronologisch sortiert.
    public static func folderName(for t: MeetingTranscript) -> String {
        "\(MeetingTranscript.Formatters.fileDate.string(from: t.date)) \(FileNames.sanitized(t.title)) · \(shortID(t.id))"
    }

    /// Die ersten 6 Zeichen der ID, klein – so stehen sie auch im Ordnernamen.
    public static func shortID(_ id: UUID) -> String {
        id.uuidString.prefix(6).lowercased()
    }

    public static func uniqueURL(in folder: URL, name: String) -> URL {
        var candidate = folder.appending(path: name, directoryHint: .isDirectory)
        var n = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appending(path: "\(name) (\(n))", directoryHint: .isDirectory)
            n += 1
        }
        return candidate
    }

    private static func readExisting(_ folder: URL) throws -> StoredMeeting {
        guard FileManager.default.fileExists(atPath: folder.appending(path: transcriptFileName).path) else {
            throw LibraryError.notFound
        }
        return try read(folder: folder)
    }
}
