import AppKit
import UniformTypeIdentifiers

/// Schreibt Meeting-Transkripte auf die Platte: automatisches Backup direkt nach der Verarbeitung
/// (Absicherung gegen versehentliches Schließen) und „Sichern unter…“ aus dem Ergebnisfenster.
enum MeetingExporter {
    /// Standard-Ablageordner, falls in den Einstellungen keiner gesetzt ist.
    static var defaultFolder: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first!
            .appendingPathComponent("LunaTranscribe Meetings", isDirectory: true)
    }

    static func folder(from settingsPath: String) -> URL {
        settingsPath.isEmpty ? defaultFolder : URL(fileURLWithPath: (settingsPath as NSString).expandingTildeInPath, isDirectory: true)
    }

    /// Legt das Backup als .txt im Ablageordner ab und gibt den Pfad zurück.
    @discardableResult
    static func writeBackup(_ transcript: MeetingTranscript, to folder: URL) throws -> URL {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = uniqueURL(in: folder, name: transcript.suggestedFileName, ext: "txt")
        try transcript.render(as: .text).write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    /// Verschiebt die Aufnahme neben das Transkript (Einstellung „Meeting-Audio behalten“).
    static func keepAudio(_ tempURL: URL, for transcript: MeetingTranscript, in folder: URL) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = uniqueURL(in: folder, name: transcript.suggestedFileName, ext: "wav")
        try FileManager.default.moveItem(at: tempURL, to: target)
    }

    /// „Sichern unter…“-Dialog. Gibt den gewählten Pfad zurück, nil bei Abbruch.
    @MainActor
    static func saveWithPanel(_ transcript: MeetingTranscript, format: MeetingTranscript.ExportFormat,
                              defaultFolder: URL) throws -> URL? {
        let panel = NSSavePanel()
        panel.title = "Meeting-Transkript sichern"
        panel.nameFieldStringValue = "\(transcript.suggestedFileName).\(format.fileExtension)"
        panel.directoryURL = defaultFolder
        panel.allowedContentTypes = [UTType(filenameExtension: format.fileExtension) ?? .plainText]
        panel.canCreateDirectories = true
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        try transcript.render(as: format).write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    /// „Meeting 2026-09-15 14-03.txt“, bei Kollision „… (2).txt“ usw.
    private static func uniqueURL(in folder: URL, name: String, ext: String) -> URL {
        var candidate = folder.appendingPathComponent("\(name).\(ext)")
        var n = 2
        while FileManager.default.fileExists(atPath: candidate.path) {
            candidate = folder.appendingPathComponent("\(name) (\(n)).\(ext)")
            n += 1
        }
        return candidate
    }
}
