import Foundation
import LunaCore

/// Die Meetings, die Claude sehen darf – aus Meeting-Ordner und Rettungsordner, **ohne vertrauliche**.
/// Liest nur geänderte `meeting.json` neu (Änderungszeit + Größe); `generation` steigt, wenn sich etwas
/// geändert hat – daran hängt der Abgleich des Suchindex und die `list_changed`-Meldung.
actor MeetingCatalog {
    struct Snapshot: Sendable {
        /// Neueste zuerst.
        let meetings: [StoredMeeting]
        let generation: Int
        let libraryFolder: URL
        let libraryReachable: Bool

        func meeting(_ id: UUID) -> StoredMeeting? { meetings.first { $0.id == id } }
    }

    private struct CacheEntry {
        let stamp: String
        let meeting: StoredMeeting?
    }

    private var cache: [String: CacheEntry] = [:]
    private var signature = ""
    private var generation = 0

    func snapshot(libraryFolder: URL, unsavedFolder: URL) -> Snapshot {
        let reachable = FileManager.default.fileExists(atPath: libraryFolder.path)
        var folders = MeetingLibrary.meetingFolders(in: libraryFolder).map { ($0, false) }
        folders += MeetingLibrary.meetingFolders(in: unsavedFolder).map { ($0, true) }

        var fresh: [String: CacheEntry] = [:]
        for (folder, unsaved) in folders {
            let stamp = Self.stamp(folder)
            if let cached = cache[folder.path], cached.stamp == stamp {
                fresh[folder.path] = cached
            } else {
                fresh[folder.path] = CacheEntry(stamp: stamp, meeting: try? MeetingLibrary.read(folder: folder, unsaved: unsaved))
            }
        }
        cache = fresh

        let newSignature = fresh.keys.sorted().map { "\($0)=\(fresh[$0]!.stamp)" }.joined(separator: "\n")
        if newSignature != signature {
            signature = newSignature
            generation += 1
        }

        // Doppelte IDs (kopierter Ordner): der zuerst gefundene gewinnt, stabil nach Pfad.
        var seen = Set<UUID>()
        let meetings = fresh.keys.sorted()
            .compactMap { fresh[$0]?.meeting }
            .filter { !$0.transcript.confidential && seen.insert($0.id).inserted }
            .sorted { $0.transcript.date > $1.transcript.date }
        return Snapshot(meetings: meetings, generation: generation, libraryFolder: libraryFolder, libraryReachable: reachable)
    }

    private static func stamp(_ folder: URL) -> String {
        let file = folder.appending(path: MeetingLibrary.transcriptFileName)
        let values = try? file.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
        let audio = FileManager.default.fileExists(atPath: folder.appending(path: MeetingLibrary.audioFileName).path)
        return "\(values?.contentModificationDate?.timeIntervalSince1970 ?? 0)|\(values?.fileSize ?? 0)|\(audio)"
    }
}

extension MeetingCatalog.Snapshot {
    /// Findet ein Meeting über die volle ID oder ein eindeutiges Präfix ab 6 Zeichen (wie im Ordnernamen).
    /// Vertrauliche Meetings gelten als nicht vorhanden – ihre Existenz wird nicht verraten.
    func resolve(_ reference: String) throws(ToolError) -> StoredMeeting {
        let needle = reference.trimmingCharacters(in: .whitespaces).lowercased()
        if let id = UUID(uuidString: needle), let meeting = meeting(id) { return meeting }
        guard needle.count >= 6 else {
            throw ToolError("„\(reference)“ ist zu kurz – gib mindestens die 6 Zeichen der Meeting-ID an (aus list_meetings oder search_meetings).")
        }
        let matches = meetings.filter { $0.id.uuidString.lowercased().hasPrefix(needle) }
        switch matches.count {
        case 1: return matches[0]
        case 0: throw ToolError("Kein Meeting mit der ID „\(reference)“. Nutze list_meetings oder search_meetings, um die ID zu finden.")
        default:
            let candidates = matches.prefix(5).map { "\($0.id.uuidString.lowercased()) („\($0.transcript.title)“)" }
            throw ToolError("Die ID „\(reference)“ ist mehrdeutig: \(candidates.joined(separator: ", ")). Gib mehr Zeichen an.")
        }
    }
}
