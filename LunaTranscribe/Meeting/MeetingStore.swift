import AppKit
import Combine

/// Die Meeting-Bibliothek: ein Unterordner pro Meeting im frei wählbaren Meeting-Ordner.
///
///     <Meeting-Ordner>/2026-09-25 14-03 Planung Q4 · a1b2c3/
///         meeting.json   – Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit)
///         summary.md     – Zusammenfassung als Markdown (für Finder, Quick Look, Claude CLI)
///         audio.wav      – nur mit „Aufnahme behalten“
///
/// Invariante: **Ein Transkript geht nie verloren.** Ist der Meeting-Ordner nicht erreichbar, landet
/// ein neues Meeting im Rettungsordner (`AppPaths.unsavedMeetings`) und zieht beim nächsten
/// erfolgreichen Laden automatisch in die Bibliothek um.
@MainActor
final class MeetingStore: ObservableObject {
    /// Kopfdaten eines Meetings für Listen – ohne die (großen) Turns.
    struct Entry: Identifiable, Equatable {
        let id: UUID
        var title: String
        let date: Date
        let durationSeconds: TimeInterval
        let speakerCount: Int
        var hasSummary: Bool
        let folder: URL
        /// Liegt im Rettungsordner, weil der Meeting-Ordner nicht erreichbar war.
        let isUnsaved: Bool
    }

    static let transcriptFileName = "meeting.json"
    static let summaryFileName = "summary.md"
    static let audioFileName = "audio.wav"

    @Published private(set) var entries: [Entry] = []
    /// Gesetzt, wenn der Meeting-Ordner nicht angelegt oder gelesen werden kann.
    @Published private(set) var folderIssue: String?

    private let settings: SettingsStore
    private var cancellables = Set<AnyCancellable>()
    /// Volltext je Meeting (klein geschrieben) für die Suche; wird bei Bedarf gefüllt.
    private var searchIndex: [UUID: String] = [:]

    var libraryFolder: URL { settings.meetingFolderURL }

    init(settings: SettingsStore) {
        self.settings = settings
        reload()
        settings.$meetingFolder
            .dropFirst()
            .removeDuplicates()
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.reload() }
            .store(in: &cancellables)
    }

    // MARK: Lesen

    /// Liest die Bibliothek neu ein: rettet liegengebliebene Meetings, importiert alte TXT-Backups.
    func reload() {
        folderIssue = nil
        do {
            try FileManager.default.createDirectory(at: libraryFolder, withIntermediateDirectories: true)
            moveUnsavedIntoLibrary()
            importLegacyTextBackups()
        } catch {
            folderIssue = "Der Meeting-Ordner ist nicht erreichbar (\(libraryFolder.path)). Neue Meetings werden vorübergehend in der App gesichert."
            Log.meeting.error("library folder unavailable: \(error.localizedDescription, privacy: .public)")
        }
        let library = folderIssue == nil ? readEntries(in: libraryFolder, unsaved: false) : []
        let unsaved = readEntries(in: AppPaths.unsavedMeetings, unsaved: true)
        entries = (library + unsaved).sorted { $0.date > $1.date }
        searchIndex = searchIndex.filter { id, _ in entries.contains { $0.id == id } }
    }

    func entry(for id: UUID) -> Entry? { entries.first { $0.id == id } }

    func load(_ id: UUID) throws -> MeetingTranscript {
        guard let entry = entry(for: id) else { throw StoreError.notFound }
        return try Self.readTranscript(in: entry.folder)
    }

    /// Meetings, deren Titel, Sprecher, Zusammenfassung oder Text `query` enthalten.
    func search(_ query: String) -> [Entry] {
        let needle = query.trimmingCharacters(in: .whitespaces).lowercased()
        guard !needle.isEmpty else { return entries }
        return entries.filter { entry in
            if entry.title.lowercased().contains(needle) { return true }
            return fullText(of: entry).contains(needle)
        }
    }

    // MARK: Schreiben

    /// Legt ein neu verarbeitetes Meeting an. `audioURL` wird in den Meeting-Ordner verschoben.
    @discardableResult
    func add(_ transcript: MeetingTranscript, audioURL: URL? = nil) -> Entry? {
        let target = folderIssue == nil ? libraryFolder : AppPaths.unsavedMeetings
        do {
            let folder = try write(transcript, into: target, existing: nil)
            if let audioURL {
                try? FileManager.default.moveItem(at: audioURL, to: folder.appending(path: Self.audioFileName))
            }
        } catch {
            // Letzte Rettung: Rettungsordner. Scheitert auch das, bleibt nur der Fehler im Log.
            Log.meeting.error("saving meeting failed: \(error.localizedDescription, privacy: .public)")
            if target != AppPaths.unsavedMeetings {
                _ = try? write(transcript, into: AppPaths.unsavedMeetings, existing: nil)
            }
        }
        reload()
        return entry(for: transcript.id)
    }

    /// Speichert Änderungen (Titel, Sprecher, Text, Zusammenfassung). Benennt den Ordner um, wenn
    /// sich der Titel geändert hat.
    func update(_ transcript: MeetingTranscript) throws {
        guard let entry = entry(for: transcript.id) else { throw StoreError.notFound }
        let parent = entry.folder.deletingLastPathComponent()
        let folder = try write(transcript, into: parent, existing: entry.folder)
        searchIndex[transcript.id] = Self.searchText(of: transcript)
        if let index = entries.firstIndex(where: { $0.id == transcript.id }) {
            entries[index] = Self.entry(for: transcript, folder: folder, unsaved: entry.isUnsaved)
        }
    }

    /// Verschiebt ein Meeting in den Papierkorb (wiederherstellbar über den Finder).
    func delete(_ id: UUID) throws {
        guard let entry = entry(for: id) else { return }
        try FileManager.default.trashItem(at: entry.folder, resultingItemURL: nil)
        entries.removeAll { $0.id == id }
        searchIndex[id] = nil
    }

    func revealInFinder(_ id: UUID) {
        guard let entry = entry(for: id) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([entry.folder])
    }

    /// Zieht alle Meetings aus `oldFolder` in den aktuell eingestellten Meeting-Ordner um.
    func moveMeetings(from oldFolder: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: libraryFolder, withIntermediateDirectories: true)
        for folder in Self.meetingFolders(in: oldFolder) {
            try fm.moveItem(at: folder, to: Self.uniqueURL(in: libraryFolder, name: folder.lastPathComponent))
        }
        reload()
    }

    // MARK: Intern

    enum StoreError: LocalizedError {
        case notFound
        var errorDescription: String? { "Das Meeting wurde nicht gefunden – vielleicht wurde es im Finder verschoben." }
    }

    /// Schreibt `meeting.json` (atomar) und `summary.md` und gibt den (ggf. umbenannten) Ordner zurück.
    private func write(_ transcript: MeetingTranscript, into parent: URL, existing: URL?) throws -> URL {
        let fm = FileManager.default
        try fm.createDirectory(at: parent, withIntermediateDirectories: true)
        let wantedName = Self.folderName(for: transcript)

        var folder: URL
        if let existing {
            folder = existing
            if existing.lastPathComponent != wantedName {
                let renamed = Self.uniqueURL(in: parent, name: wantedName)
                try fm.moveItem(at: existing, to: renamed)
                folder = renamed
            }
        } else {
            folder = Self.uniqueURL(in: parent, name: wantedName)
            try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        }

        let data = try Self.encoder.encode(transcript)
        try data.write(to: folder.appending(path: Self.transcriptFileName), options: .atomic)

        let summaryURL = folder.appending(path: Self.summaryFileName)
        if let summary = transcript.summary {
            let markdown = "# \(transcript.title) – Zusammenfassung\n\n\(summary.markdown)\n"
            try markdown.write(to: summaryURL, atomically: true, encoding: .utf8)
        } else if fm.fileExists(atPath: summaryURL.path) {
            try fm.removeItem(at: summaryURL)
        }
        return folder
    }

    private func readEntries(in parent: URL, unsaved: Bool) -> [Entry] {
        Self.meetingFolders(in: parent).compactMap { folder in
            do {
                return Self.entry(for: try Self.readTranscript(in: folder), folder: folder, unsaved: unsaved)
            } catch {
                Log.meeting.error("unreadable meeting \(folder.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
                return nil
            }
        }
    }

    private func fullText(of entry: Entry) -> String {
        if let cached = searchIndex[entry.id] { return cached }
        let text = (try? Self.readTranscript(in: entry.folder)).map(Self.searchText(of:)) ?? ""
        searchIndex[entry.id] = text
        return text
    }

    private func moveUnsavedIntoLibrary() {
        for folder in Self.meetingFolders(in: AppPaths.unsavedMeetings) {
            do {
                try FileManager.default.moveItem(at: folder, to: Self.uniqueURL(in: libraryFolder, name: folder.lastPathComponent))
                Log.meeting.notice("rescued meeting moved into library: \(folder.lastPathComponent, privacy: .public)")
            } catch {
                Log.meeting.error("could not move rescued meeting: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Einmaliger Import der TXT-Backups aus Version 0.1 („Meeting 2026-09-15 14-03.txt“).
    /// Die Originale bleiben liegen; `importedFrom` verhindert einen zweiten Import.
    private func importLegacyTextBackups() {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: libraryFolder, includingPropertiesForKeys: nil) else { return }
        let legacy = files.filter { $0.pathExtension == "txt" && $0.lastPathComponent.hasPrefix("Meeting ") }
        guard !legacy.isEmpty else { return }

        let alreadyImported = Set(Self.meetingFolders(in: libraryFolder).compactMap {
            (try? Self.readTranscript(in: $0))?.importedFrom
        })
        for file in legacy where !alreadyImported.contains(file.lastPathComponent) {
            guard let text = try? String(contentsOf: file, encoding: .utf8),
                  let transcript = LegacyMeetingImporter.parse(text, fileName: file.lastPathComponent) else { continue }
            do {
                _ = try write(transcript, into: libraryFolder, existing: nil)
                Log.meeting.notice("imported legacy backup \(file.lastPathComponent, privacy: .public)")
            } catch {
                Log.meeting.error("legacy import failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    private static func entry(for t: MeetingTranscript, folder: URL, unsaved: Bool) -> Entry {
        Entry(id: t.id, title: t.title, date: t.date, durationSeconds: t.durationSeconds,
              speakerCount: t.speakerIDs.count, hasSummary: t.summary != nil, folder: folder, isUnsaved: unsaved)
    }

    private static func searchText(of t: MeetingTranscript) -> String {
        ([t.title, t.summary?.markdown ?? ""] + t.speakerNames.values + t.turns.map(\.text))
            .joined(separator: "\n").lowercased()
    }

    private static func readTranscript(in folder: URL) throws -> MeetingTranscript {
        let data = try Data(contentsOf: folder.appending(path: transcriptFileName))
        return try decoder.decode(MeetingTranscript.self, from: data)
    }

    private static func meetingFolders(in parent: URL) -> [URL] {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: parent, includingPropertiesForKeys: [.isDirectoryKey],
                                                       options: [.skipsHiddenFiles]) else { return [] }
        return items.filter { fm.fileExists(atPath: $0.appending(path: transcriptFileName).path) }
    }

    /// „2026-09-25 14-03 Planung Q4 · a1b2c3“ – Datum zuerst, damit der Finder chronologisch sortiert.
    private static func folderName(for t: MeetingTranscript) -> String {
        let shortID = t.id.uuidString.prefix(6).lowercased()
        return "\(MeetingTranscript.Formatters.fileDate.string(from: t.date)) \(FileNames.sanitized(t.title)) · \(shortID)"
    }

    private static func uniqueURL(in folder: URL, name: String) -> URL {
        var candidate = folder.appending(path: name, directoryHint: .isDirectory)
        var n = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appending(path: "\(name) (\(n))", directoryHint: .isDirectory)
            n += 1
        }
        return candidate
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
}
