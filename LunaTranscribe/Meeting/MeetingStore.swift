import AppKit
import Combine
import LunaCore

/// Die Meeting-Bibliothek: ein Unterordner pro Meeting im frei wählbaren Meeting-Ordner.
///
///     <Meeting-Ordner>/2026-09-25 14-03 Planung Q4 · a1b2c3/
///         meeting.json   – Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit)
///         summary.md     – Zusammenfassung als Markdown (für Finder, Quick Look, Claude CLI)
///         audio.wav      – nur mit „Aufnahme behalten“
///
/// Lesen und Schreiben laufen über `MeetingLibrary` (LunaCore) – dieselben Funktionen nutzt luna-mcp.
///
/// Invariante: **Ein Transkript geht nie verloren.** Ist der Meeting-Ordner nicht erreichbar, landet
/// ein neues Meeting im Rettungsordner (`AppPaths.unsavedMeetings`) und zieht beim nächsten
/// erfolgreichen Laden automatisch in die Bibliothek um.
///
/// Änderungen von außen (Claude über luna-mcp: umbenennen, Sprecher benennen, löschen) meldet das
/// Aktivitätsprotokoll; der Store lädt dann neu und sendet `externalChanges`.
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
        var isConfidential: Bool
        let folder: URL
        /// Liegt im Rettungsordner, weil der Meeting-Ordner nicht erreichbar war.
        let isUnsaved: Bool
    }

    static let transcriptFileName = MeetingLibrary.transcriptFileName
    static let summaryFileName = MeetingLibrary.summaryFileName
    static let audioFileName = MeetingLibrary.audioFileName

    @Published private(set) var entries: [Entry] = []
    /// Gesetzt, wenn der Meeting-Ordner nicht angelegt oder gelesen werden kann.
    @Published private(set) var folderIssue: String?

    /// Meetings, die Claude über luna-mcp geändert oder gelöscht hat (nach dem Neuladen gesendet).
    let externalChanges = PassthroughSubject<Set<UUID>, Never>()

    private let settings: SettingsStore
    private var cancellables = Set<AnyCancellable>()
    /// Volltext je Meeting (klein geschrieben) für die Suche; wird bei Bedarf gefüllt.
    private var searchIndex: [UUID: String] = [:]
    private let activityLog = ActivityLog()
    /// Zuletzt gesehene Änderung im Aktivitätsprotokoll.
    private var lastActivity = Date()

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
        watchActivityLog()
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
        try MeetingLibrary.readTranscript(in: try currentFolder(for: id))
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
            let folder = try MeetingLibrary.write(transcript, into: target, existing: nil)
            if let audioURL {
                try? FileManager.default.moveItem(at: audioURL, to: folder.appending(path: Self.audioFileName))
            }
        } catch {
            // Letzte Rettung: Rettungsordner. Scheitert auch das, bleibt nur der Fehler im Log.
            Log.meeting.error("saving meeting failed: \(error.localizedDescription, privacy: .public)")
            if target != AppPaths.unsavedMeetings {
                _ = try? MeetingLibrary.write(transcript, into: AppPaths.unsavedMeetings, existing: nil)
            }
        }
        reload()
        return entry(for: transcript.id)
    }

    /// Speichert Änderungen (Titel, Sprecher, Text, Zusammenfassung). Benennt den Ordner um, wenn
    /// sich der Titel geändert hat.
    func update(_ transcript: MeetingTranscript) throws {
        let current = try currentFolder(for: transcript.id)
        let isUnsaved = entry(for: transcript.id)?.isUnsaved ?? false
        let folder = try MeetingLibrary.write(transcript, into: current.deletingLastPathComponent(), existing: current)
        searchIndex[transcript.id] = Self.searchText(of: transcript)
        if let index = entries.firstIndex(where: { $0.id == transcript.id }) {
            entries[index] = Self.entry(for: transcript, folder: folder, unsaved: isUnsaved)
        }
    }

    /// Speichert Änderungen aus einem geöffneten Meeting. Hat luna-mcp die Datei seit `base` (dem zuletzt
    /// geladenen oder gespeicherten Stand) geändert, werden beide Seiten zusammengeführt: Was der Nutzer
    /// geändert hat, gewinnt, alles andere kommt von der Platte. Gibt den gespeicherten Stand zurück.
    func save(_ local: MeetingTranscript, base: MeetingTranscript) throws -> MeetingTranscript {
        let disk = try load(local.id)
        let merged = disk == base ? local : MeetingLibrary.merge(local: local, base: base, disk: disk)
        if merged != disk { try update(merged) }
        return merged
    }

    /// Verschiebt ein Meeting in den Papierkorb (wiederherstellbar über den Finder).
    func delete(_ id: UUID) throws {
        guard entry(for: id) != nil else { return }
        try MeetingLibrary.moveToTrash(try currentFolder(for: id))
        entries.removeAll { $0.id == id }
        searchIndex[id] = nil
    }

    func revealInFinder(_ id: UUID) {
        guard let folder = try? currentFolder(for: id) else { return }
        NSWorkspace.shared.activateFileViewerSelecting([folder])
    }

    /// Zieht alle Meetings aus `oldFolder` in den aktuell eingestellten Meeting-Ordner um.
    func moveMeetings(from oldFolder: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: libraryFolder, withIntermediateDirectories: true)
        for folder in MeetingLibrary.meetingFolders(in: oldFolder) {
            try fm.moveItem(at: folder, to: MeetingLibrary.uniqueURL(in: libraryFolder, name: folder.lastPathComponent))
        }
        reload()
    }

    /// Macht eine Umbenennung durch Claude rückgängig – nur, wenn seitdem niemand denselben Wert geändert hat.
    func undo(_ change: ActivityEntry) throws {
        guard let id = change.meetingIDs.first, let old = change.oldValue else { return }
        var transcript = try load(id)
        switch change.action {
        case .renameMeeting:
            guard transcript.title == change.newValue else { throw UndoError.changedSince }
            transcript.title = old
        case .renameSpeaker:
            guard let speaker = change.speakerID, transcript.displayName(for: speaker) == change.newValue else {
                throw UndoError.changedSince
            }
            if old == speaker { transcript.speakerNames.removeValue(forKey: speaker) } else { transcript.speakerNames[speaker] = old }
        case .read, .search, .delete, .deleteDeclined:
            return
        }
        try update(transcript)
        externalChanges.send([id])
    }

    enum UndoError: LocalizedError {
        case changedSince
        var errorDescription: String? { "Der Wert wurde seitdem erneut geändert – bitte direkt im Meeting anpassen." }
    }

    /// Alle Meetings mit Inhalt (für den Suchindex). Liest von der Platte – im Hintergrund aufrufen.
    nonisolated static func storedMeetings(libraryFolder: URL) -> [StoredMeeting] {
        MeetingLibrary.scan(libraryFolder) + MeetingLibrary.scan(AppPaths.unsavedMeetings, unsaved: true)
    }

    // MARK: Intern

    enum StoreError: LocalizedError {
        case notFound
        var errorDescription: String? { "Das Meeting wurde nicht gefunden – vielleicht wurde es im Finder verschoben oder gelöscht." }
    }

    /// Ordner eines Meetings. Hat ihn jemand von außen umbenannt (luna-mcp, Finder), wird neu eingelesen.
    private func currentFolder(for id: UUID) throws -> URL {
        if let folder = entry(for: id)?.folder,
           FileManager.default.fileExists(atPath: folder.appending(path: Self.transcriptFileName).path) {
            return folder
        }
        reload()
        guard let folder = entry(for: id)?.folder else { throw StoreError.notFound }
        return folder
    }

    private func readEntries(in parent: URL, unsaved: Bool) -> [Entry] {
        MeetingLibrary.scan(parent, unsaved: unsaved) { folder, error in
            Log.meeting.error("unreadable meeting \(folder.lastPathComponent, privacy: .public): \(error.localizedDescription, privacy: .public)")
        }
        .map { Self.entry(for: $0.transcript, folder: $0.folder, unsaved: unsaved) }
    }

    private func fullText(of entry: Entry) -> String {
        if let cached = searchIndex[entry.id] { return cached }
        let text = (try? MeetingLibrary.readTranscript(in: entry.folder)).map(Self.searchText(of:)) ?? ""
        searchIndex[entry.id] = text
        return text
    }

    private func moveUnsavedIntoLibrary() {
        for folder in MeetingLibrary.meetingFolders(in: AppPaths.unsavedMeetings) {
            do {
                try FileManager.default.moveItem(at: folder, to: MeetingLibrary.uniqueURL(in: libraryFolder, name: folder.lastPathComponent))
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

        let alreadyImported = Set(MeetingLibrary.scan(libraryFolder).compactMap(\.transcript.importedFrom))
        for file in legacy where !alreadyImported.contains(file.lastPathComponent) {
            guard let text = try? String(contentsOf: file, encoding: .utf8),
                  let transcript = LegacyMeetingImporter.parse(text, fileName: file.lastPathComponent) else { continue }
            do {
                try MeetingLibrary.write(transcript, into: libraryFolder, existing: nil)
                Log.meeting.notice("imported legacy backup \(file.lastPathComponent, privacy: .public)")
            } catch {
                Log.meeting.error("legacy import failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Beobachtet das Aktivitätsprotokoll von luna-mcp. Alle zwei Sekunden reicht: Claude ändert selten,
    /// und der Blick auf Änderungszeit und Größe einer Datei kostet praktisch nichts.
    private func watchActivityLog() {
        Timer.publish(every: 2, on: .main, in: .common)
            .autoconnect()
            .compactMap { [activityLog] _ in
                (try? activityLog.url.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            }
            .removeDuplicates()
            .sink { [weak self] _ in self?.applyExternalChanges() }
            .store(in: &cancellables)
    }

    private func applyExternalChanges() {
        let changes = activityLog.entries(limit: 200).filter { $0.isChange && $0.date > lastActivity }
        guard let newest = changes.first?.date else { return }
        lastActivity = newest
        reload()
        let ids = Set(changes.flatMap(\.meetingIDs))
        Log.meeting.notice("external changes by Claude: \(ids.count) meeting(s)")
        externalChanges.send(ids)
    }

    private static func entry(for t: MeetingTranscript, folder: URL, unsaved: Bool) -> Entry {
        Entry(id: t.id, title: t.title, date: t.date, durationSeconds: t.durationSeconds,
              speakerCount: t.speakerIDs.count, hasSummary: t.summary != nil, isConfidential: t.confidential,
              folder: folder, isUnsaved: unsaved)
    }

    private static func searchText(of t: MeetingTranscript) -> String {
        ([t.title, t.summary?.markdown ?? ""] + t.speakerNames.values + t.turns.map(\.text))
            .joined(separator: "\n").lowercased()
    }
}
