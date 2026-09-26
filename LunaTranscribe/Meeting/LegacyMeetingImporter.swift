import Foundation
import LunaCore

/// Liest die TXT-Backups aus Version 0.1 wieder ein. Format:
///
///     LunaTranscribe – Meeting vom 15.09.2026, 14:03 (Dauer 12:34, Parakeet v3, Deutsch)
///
///     [00:05] Sprecher 1: Hallo zusammen …
///     [01:12] Anna: …
enum LegacyMeetingImporter {
    static func parse(_ text: String, fileName: String) -> MeetingTranscript? {
        var lines = text.components(separatedBy: .newlines)
        guard let header = lines.first, header.hasPrefix("LunaTranscribe – Meeting vom ") else { return nil }
        lines.removeFirst()

        let (date, duration, engine, language) = parseHeader(header)
        var parsed: [(start: TimeInterval, speaker: String, text: String)] = []
        for line in lines {
            guard let match = line.firstMatch(of: turnPattern),
                  let start = seconds(String(match.1)) else { continue }
            parsed.append((start, String(match.2), String(match.3)))
        }
        guard !parsed.isEmpty, let date else { return nil }

        let total = duration ?? (parsed.last?.start ?? 0)
        let turns = parsed.enumerated().map { index, turn in
            let end = index + 1 < parsed.count ? parsed[index + 1].start : total
            return MeetingTranscript.Turn(speakerID: turn.speaker, start: turn.start, end: end, text: turn.text)
        }
        return MeetingTranscript(date: date, durationSeconds: total, engine: engine, language: language,
                                 turns: turns, importedFrom: fileName)
    }

    // „[mm:ss] Name: Text“ oder „[h:mm:ss] Name: Text“
    private static let turnPattern = #/^\[([0-9:]+)\] ([^:]+): (.*)$/#

    private static func parseHeader(_ header: String) -> (Date?, TimeInterval?, EngineKind, AppLanguage) {
        let body = header.dropFirst("LunaTranscribe – Meeting vom ".count)
        let parts = body.split(separator: "(", maxSplits: 1)
        let date = parts.first.flatMap { MeetingTranscript.Formatters.headerDate.date(from: $0.trimmingCharacters(in: .whitespaces)) }

        var duration: TimeInterval?
        var engine = EngineKind.parakeet
        var language = AppLanguage.german
        if parts.count == 2 {
            let details = parts[1].trimmingCharacters(in: CharacterSet(charactersIn: ") ")).components(separatedBy: ", ")
            for item in details {
                if item.hasPrefix("Dauer ") { duration = seconds(String(item.dropFirst("Dauer ".count))) }
                if let kind = EngineKind.allCases.first(where: { $0.shortLabel == item }) { engine = kind }
                // Alte Backups nutzten den Sprachnamen der Systemsprache („German“), neue den deutschen.
                if let lang = AppLanguage.allCases.first(where: {
                    $0.displayName == item || Locale.current.localizedString(forLanguageCode: $0.rawValue)?.capitalized == item
                }) { language = lang }
            }
        }
        return (date, duration, engine, language)
    }

    /// „12:34“ oder „1:02:03“ → Sekunden
    private static func seconds(_ clock: String) -> TimeInterval? {
        let parts = clock.split(separator: ":").compactMap { Int($0) }
        guard (2...3).contains(parts.count) else { return nil }
        return TimeInterval(parts.reduce(0) { $0 * 60 + $1 })
    }
}
