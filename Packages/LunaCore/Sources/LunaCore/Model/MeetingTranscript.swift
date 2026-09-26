import Foundation

/// Ein Meeting: Redebeiträge in zeitlicher Reihenfolge plus Metadaten. Wird als `meeting.json` im
/// Meeting-Ordner gespeichert (`MeetingLibrary`); TXT/Markdown entstehen nur beim Export.
///
/// Sprecher werden über stabile IDs („Sprecher 1“ …) geführt; `speakerNames` erlaubt das Umbenennen,
/// ohne die Turns anzufassen.
public struct MeetingTranscript: Codable, Identifiable, Equatable, Sendable {
    /// Version des JSON-Formats. Bei inkompatiblen Änderungen erhöhen und in `MeetingLibrary` migrieren.
    /// Neue optionale Felder (wie `isConfidential`) brauchen keine neue Version.
    public static let currentSchemaVersion = 1

    public struct Turn: Codable, Identifiable, Equatable, Sendable {
        public var id: UUID
        public let speakerID: String
        public let start: TimeInterval
        public let end: TimeInterval
        /// Vom Nutzer korrigierbar; Korrekturen speisen das lernende Wörterbuch.
        public var text: String

        public init(id: UUID = UUID(), speakerID: String, start: TimeInterval, end: TimeInterval, text: String) {
            self.id = id
            self.speakerID = speakerID
            self.start = start
            self.end = end
            self.text = text
        }
    }

    public var schemaVersion = currentSchemaVersion
    public var id = UUID()
    public var title: String
    public let date: Date
    public let durationSeconds: TimeInterval
    public let engine: EngineKind
    public let language: AppLanguage
    public var turns: [Turn]
    /// Umbenennungen: Sprecher-ID → Anzeigename. Fehlt ein Eintrag, gilt die ID selbst.
    public var speakerNames: [String: String] = [:]
    public var summary: MeetingSummary?
    /// Dateiname eines alten TXT-Backups, aus dem dieses Meeting importiert wurde (verhindert Doppelimporte).
    public var importedFrom: String?
    /// Vertraulich: für Claude (MCP) unsichtbar, nicht im Suchindex, keine Cloud-Zusammenfassung.
    /// Optional, damit ältere Dateien ohne das Feld gültig bleiben (nil = nicht vertraulich).
    public var isConfidential: Bool?

    public init(title: String? = nil, date: Date, durationSeconds: TimeInterval, engine: EngineKind,
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
    public var speakerIDs: [String] {
        var seen = Set<String>()
        return turns.compactMap { seen.insert($0.speakerID).inserted ? $0.speakerID : nil }
    }

    public func displayName(for speakerID: String) -> String {
        let custom = speakerNames[speakerID]?.trimmingCharacters(in: .whitespaces) ?? ""
        return custom.isEmpty ? speakerID : custom
    }

    public var confidential: Bool { isConfidential == true }

    public var wordCount: Int { turns.reduce(0) { $0 + $1.text.split(whereSeparator: \.isWhitespace).count } }

    public static func defaultTitle(for date: Date) -> String {
        "Meeting vom \(Formatters.headerDate.string(from: date))"
    }

    // MARK: Export

    public enum ExportFormat: String, CaseIterable, Identifiable, Sendable {
        case text
        case markdown

        public var id: String { rawValue }
        public var label: String {
            switch self {
            case .text: return "Text (.txt)"
            case .markdown: return "Markdown (.md)"
            }
        }
        public var fileExtension: String {
            switch self {
            case .text: return "txt"
            case .markdown: return "md"
            }
        }
    }

    public func render(as format: ExportFormat, includeSummary: Bool = false) -> String {
        switch format {
        case .text: return renderText(includeSummary: includeSummary)
        case .markdown: return renderMarkdown(includeSummary: includeSummary)
        }
    }

    /// Dateiname ohne Endung, z. B. „2026-09-15 14-03 Planung Q4“.
    public var suggestedFileName: String {
        "\(Formatters.fileDate.string(from: date)) \(FileNames.sanitized(title))"
    }

    /// Kopfzeile mit Metadaten, von allen Formaten gemeinsam genutzt.
    public var metadataLine: String {
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

    public static func demoteHeadings(_ markdown: String) -> String {
        markdown.split(separator: "\n", omittingEmptySubsequences: false)
            .map { $0.hasPrefix("#") ? "#" + $0 : String($0) }
            .joined(separator: "\n")
    }

    // MARK: Formatierung

    /// hh:mm:ss – Stunden werden nur gezeigt, wenn das Meeting so lang war.
    public static func clock(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }

    public enum Formatters {
        // DateFormatter ist zum Formatieren threadsicher, solange niemand die Konfiguration ändert.
        public nonisolated(unsafe) static let fileDate: DateFormatter = {
            let f = DateFormatter()
            f.locale = Locale(identifier: "en_US_POSIX")
            f.dateFormat = "yyyy-MM-dd HH-mm"
            return f
        }()

        public nonisolated(unsafe) static let headerDate: DateFormatter = {
            let f = DateFormatter()
            f.locale = Locale(identifier: "de_DE")
            f.dateFormat = "dd.MM.yyyy, HH:mm"
            return f
        }()
    }
}

/// Von der Claude CLI erzeugte Zusammenfassung eines Meetings (Markdown).
public struct MeetingSummary: Codable, Equatable, Sendable {
    public var markdown: String
    public var createdAt: Date
    /// Modell, das in den Einstellungen gewählt war; nil = Standard der CLI.
    public var model: String?

    public init(markdown: String, createdAt: Date, model: String? = nil) {
        self.markdown = markdown
        self.createdAt = createdAt
        self.model = model
    }
}

public enum FileNames {
    /// Macht einen Titel dateinamen-tauglich: keine Pfadtrenner, keine Steuerzeichen, begrenzte Länge.
    public static func sanitized(_ name: String, maxLength: Int = 60) -> String {
        let forbidden = CharacterSet(charactersIn: "/\\:?%*|\"<>").union(.controlCharacters).union(.newlines)
        let cleaned = name.components(separatedBy: forbidden).joined(separator: "-")
            .trimmingCharacters(in: .whitespaces.union(CharacterSet(charactersIn: ".")))
        let short = String(cleaned.prefix(maxLength)).trimmingCharacters(in: .whitespaces)
        return short.isEmpty ? "Meeting" : short
    }
}
