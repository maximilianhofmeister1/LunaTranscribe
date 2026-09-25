import Foundation

/// Ein Meeting: Redebeiträge in zeitlicher Reihenfolge plus Metadaten. Wird als `meeting.json` im
/// Meeting-Ordner gespeichert (`MeetingStore`); TXT/Markdown entstehen nur beim Export.
///
/// Sprecher werden über stabile IDs („Sprecher 1“ …) geführt; `speakerNames` erlaubt das Umbenennen,
/// ohne die Turns anzufassen.
struct MeetingTranscript: Codable, Identifiable, Equatable {
    /// Version des JSON-Formats. Bei inkompatiblen Änderungen erhöhen und in `MeetingStore` migrieren.
    static let currentSchemaVersion = 1

    struct Turn: Codable, Identifiable, Equatable {
        var id = UUID()
        let speakerID: String
        let start: TimeInterval
        let end: TimeInterval
        /// Vom Nutzer korrigierbar; Korrekturen speisen das lernende Wörterbuch.
        var text: String
    }

    var schemaVersion = currentSchemaVersion
    var id = UUID()
    var title: String
    let date: Date
    let durationSeconds: TimeInterval
    let engine: EngineKind
    let language: AppLanguage
    var turns: [Turn]
    /// Umbenennungen: Sprecher-ID → Anzeigename. Fehlt ein Eintrag, gilt die ID selbst.
    var speakerNames: [String: String] = [:]
    var summary: MeetingSummary?
    /// Dateiname eines alten TXT-Backups, aus dem dieses Meeting importiert wurde (verhindert Doppelimporte).
    var importedFrom: String?

    init(title: String? = nil, date: Date, durationSeconds: TimeInterval, engine: EngineKind,
         language: AppLanguage, turns: [Turn], speakerNames: [String: String] = [:], importedFrom: String? = nil) {
        self.title = title ?? Self.defaultTitle(for: date)
        self.date = date
        self.durationSeconds = durationSeconds
        self.engine = engine
        self.language = language
        self.turns = turns
        self.speakerNames = speakerNames
        self.importedFrom = importedFrom
    }

    /// Sprecher-IDs in der Reihenfolge ihres ersten Auftretens.
    var speakerIDs: [String] {
        var seen = Set<String>()
        return turns.compactMap { seen.insert($0.speakerID).inserted ? $0.speakerID : nil }
    }

    func displayName(for speakerID: String) -> String {
        let custom = speakerNames[speakerID]?.trimmingCharacters(in: .whitespaces) ?? ""
        return custom.isEmpty ? speakerID : custom
    }

    var wordCount: Int { turns.reduce(0) { $0 + $1.text.split(whereSeparator: \.isWhitespace).count } }

    static func defaultTitle(for date: Date) -> String {
        "Meeting vom \(Formatters.headerDate.string(from: date))"
    }

    // MARK: Export

    enum ExportFormat: String, CaseIterable, Identifiable {
        case text
        case markdown

        var id: String { rawValue }
        var label: String {
            switch self {
            case .text: return "Text (.txt)"
            case .markdown: return "Markdown (.md)"
            }
        }
        var fileExtension: String {
            switch self {
            case .text: return "txt"
            case .markdown: return "md"
            }
        }
    }

    func render(as format: ExportFormat, includeSummary: Bool = false) -> String {
        switch format {
        case .text: return renderText(includeSummary: includeSummary)
        case .markdown: return renderMarkdown(includeSummary: includeSummary)
        }
    }

    /// Dateiname ohne Endung, z. B. „2026-09-15 14-03 Planung Q4“.
    var suggestedFileName: String {
        "\(Formatters.fileDate.string(from: date)) \(FileNames.sanitized(title))"
    }

    /// Kopfzeile mit Metadaten, von allen Formaten gemeinsam genutzt.
    private var metadataLine: String {
        "\(Formatters.headerDate.string(from: date)) · Dauer \(Self.clock(durationSeconds)) · \(engine.shortLabel) · \(language.displayName)"
    }

    private func renderText(includeSummary: Bool) -> String {
        var lines = [title, metadataLine, ""]
        if includeSummary, let summary {
            lines += ["ZUSAMMENFASSUNG", "", summary.markdown, "", "TRANSKRIPT", ""]
        }
        for turn in turns {
            lines.append("[\(Self.clock(turn.start))] \(displayName(for: turn.speakerID)): \(turn.text)")
        }
        return lines.joined(separator: "\n") + "\n"
    }

    private func renderMarkdown(includeSummary: Bool) -> String {
        var lines = ["# \(title)", "", "_\(metadataLine)_", ""]
        if includeSummary, let summary {
            // Überschriften der Zusammenfassung eine Ebene tiefer, damit „# Titel“ oben bleibt.
            lines += ["## Zusammenfassung", "", Self.demoteHeadings(summary.markdown), "", "## Transkript", ""]
        }
        for turn in turns {
            lines.append("**\(displayName(for: turn.speakerID))** (\(Self.clock(turn.start))): \(turn.text)")
            lines.append("")
        }
        return lines.joined(separator: "\n")
    }

    private static func demoteHeadings(_ markdown: String) -> String {
        markdown.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.hasPrefix("#") ? "#" + $0 : String($0) }
            .joined(separator: "\n")
    }

    // MARK: Formatierung

    /// hh:mm:ss – Stunden werden nur gezeigt, wenn das Meeting so lang war.
    static func clock(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }

    enum Formatters {
        static let fileDate: DateFormatter = {
            let f = DateFormatter()
            f.locale = Locale(identifier: "en_US_POSIX")
            f.dateFormat = "yyyy-MM-dd HH-mm"
            return f
        }()

        static let headerDate: DateFormatter = {
            let f = DateFormatter()
            f.locale = Locale(identifier: "de_DE")
            f.dateFormat = "dd.MM.yyyy, HH:mm"
            return f
        }()
    }
}

/// Von der Claude CLI erzeugte Zusammenfassung eines Meetings (Markdown).
struct MeetingSummary: Codable, Equatable {
    var markdown: String
    var createdAt: Date
    /// Modell, das in den Einstellungen gewählt war; nil = Standard der CLI.
    var model: String?
}

extension EngineKind: Codable {}

enum FileNames {
    /// Macht einen Titel dateinamen-tauglich: keine Pfadtrenner, keine Steuerzeichen, begrenzte Länge.
    static func sanitized(_ name: String, maxLength: Int = 60) -> String {
        let forbidden = CharacterSet(charactersIn: "/\\:?%*|\"<>").union(.controlCharacters).union(.newlines)
        let cleaned = name.components(separatedBy: forbidden).joined(separator: "-")
            .trimmingCharacters(in: .whitespaces.union(CharacterSet(charactersIn: ".")))
        let short = String(cleaned.prefix(maxLength)).trimmingCharacters(in: .whitespaces)
        return short.isEmpty ? "Meeting" : short
    }
}
