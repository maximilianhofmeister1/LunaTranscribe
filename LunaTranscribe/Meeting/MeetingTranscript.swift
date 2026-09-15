import Foundation

/// Fertiges Meeting-Transkript: Redebeiträge in zeitlicher Reihenfolge plus Metadaten.
/// Sprecher werden intern über stabile IDs („Sprecher 1“ …) geführt; `speakerNames` erlaubt das
/// Umbenennen im Ergebnisfenster, ohne die Turns anzufassen.
struct MeetingTranscript {
    struct Turn: Identifiable {
        let id = UUID()
        let speakerID: String
        let start: TimeInterval
        let end: TimeInterval
        let text: String
    }

    let date: Date
    let durationSeconds: TimeInterval
    let engine: EngineKind
    let language: AppLanguage
    let turns: [Turn]
    /// Umbenennungen: Sprecher-ID → Anzeigename. Fehlt ein Eintrag, gilt die ID selbst.
    var speakerNames: [String: String] = [:]

    /// Sprecher-IDs in der Reihenfolge ihres ersten Auftretens.
    var speakerIDs: [String] {
        var seen = Set<String>()
        return turns.compactMap { seen.insert($0.speakerID).inserted ? $0.speakerID : nil }
    }

    func displayName(for speakerID: String) -> String {
        let custom = speakerNames[speakerID]?.trimmingCharacters(in: .whitespaces) ?? ""
        return custom.isEmpty ? speakerID : custom
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

    func render(as format: ExportFormat) -> String {
        switch format {
        case .text: return renderText()
        case .markdown: return renderMarkdown()
        }
    }

    /// Dateiname ohne Endung, z. B. „Meeting 2026-09-15 14-03“.
    var suggestedFileName: String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH-mm"
        return "Meeting \(f.string(from: date))"
    }

    private func renderText() -> String {
        var lines = ["LunaTranscribe – Meeting vom \(Self.headerDate(date)) (Dauer \(Self.clock(durationSeconds)), \(engine.shortLabel), \(language.displayName))", ""]
        for turn in turns {
            lines.append("[\(Self.clock(turn.start))] \(displayName(for: turn.speakerID)): \(turn.text)")
        }
        return lines.joined(separator: "\n") + "\n"
    }

    private func renderMarkdown() -> String {
        var lines = [
            "# Meeting \(Self.headerDate(date))",
            "",
            "Dauer: \(Self.clock(durationSeconds)) · Engine: \(engine.shortLabel) · Sprache: \(language.displayName)",
            "",
        ]
        for turn in turns {
            lines.append("**\(displayName(for: turn.speakerID))** (\(Self.clock(turn.start))): \(turn.text)")
            lines.append("")
        }
        return lines.joined(separator: "\n")
    }

    // MARK: Formatierung

    /// hh:mm:ss – Stunden werden nur gezeigt, wenn das Meeting so lang war.
    static func clock(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded(.down))
        let h = total / 3600, m = (total % 3600) / 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%02d:%02d", m, s)
    }

    private static func headerDate(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        f.dateFormat = "dd.MM.yyyy, HH:mm"
        return f.string(from: date)
    }
}
