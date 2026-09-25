import AppKit
import UniformTypeIdentifiers

/// Export eines Meetings als TXT oder Markdown über den „Sichern“-Dialog. Gespeichert wird ein
/// Meeting ohnehin als JSON in der Bibliothek (`MeetingStore`); Export ist nur für die Weitergabe.
enum MeetingExporter {
    /// Gibt den gewählten Pfad zurück, nil bei Abbruch.
    @MainActor
    static func saveWithPanel(_ transcript: MeetingTranscript, format: MeetingTranscript.ExportFormat,
                              includeSummary: Bool, startIn folder: URL) throws -> URL? {
        let panel = NSSavePanel()
        panel.title = "Meeting exportieren"
        panel.nameFieldStringValue = "\(transcript.suggestedFileName).\(format.fileExtension)"
        panel.directoryURL = folder
        panel.allowedContentTypes = [UTType(filenameExtension: format.fileExtension) ?? .plainText]
        panel.canCreateDirectories = true
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        try transcript.render(as: format, includeSummary: includeSummary).write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    @MainActor
    static func copyToPasteboard(_ transcript: MeetingTranscript, includeSummary: Bool) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(transcript.render(as: .markdown, includeSummary: includeSummary), forType: .string)
    }
}
