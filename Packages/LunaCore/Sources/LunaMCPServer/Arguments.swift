import Foundation
import MCP

/// Typisierter Zugriff auf Tool- und Prompt-Argumente mit Fehlermeldungen, die Claude weiterhelfen.
struct Arguments: Sendable {
    let values: [String: Value]
    let timeZone: TimeZone

    init(_ values: [String: Value]?, timeZone: TimeZone) {
        self.values = values ?? [:]
        self.timeZone = timeZone
    }

    /// Für Prompts: dort sind alle Argumente Texte.
    init(strings: [String: String]?, timeZone: TimeZone) {
        self.values = (strings ?? [:]).mapValues { .string($0) }
        self.timeZone = timeZone
    }

    func string(_ key: String) throws(ToolError) -> String? {
        switch values[key] {
        case nil, .null: return nil
        case .string(let s):
            let trimmed = s.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        case .int(let i): return String(i)
        default: throw ToolError("„\(key)“ muss ein Text sein.")
        }
    }

    func requiredString(_ key: String) throws(ToolError) -> String {
        guard let value = try string(key) else { throw ToolError("„\(key)“ fehlt.") }
        return value
    }

    /// Text, der leer sein darf (z. B. Sprechername zurücksetzen). Fehlt der Schlüssel ganz, ist das ein Fehler.
    func presentString(_ key: String) throws(ToolError) -> String {
        guard values[key] != nil else { throw ToolError("„\(key)“ fehlt.") }
        return try string(key) ?? ""
    }

    func int(_ key: String, default fallback: Int, range: ClosedRange<Int>) throws(ToolError) -> Int {
        let value: Int
        switch values[key] {
        case nil, .null: return fallback
        case .int(let i): value = i
        case .double(let d) where d == d.rounded(): value = Int(d)
        case .string(let s) where Int(s) != nil: value = Int(s)!
        default: throw ToolError("„\(key)“ muss eine ganze Zahl sein.")
        }
        guard range.contains(value) else {
            throw ToolError("„\(key)“ muss zwischen \(range.lowerBound) und \(range.upperBound) liegen.")
        }
        return value
    }

    func bool(_ key: String) throws(ToolError) -> Bool? {
        switch values[key] {
        case nil, .null: return nil
        case .bool(let b): return b
        case .string("true"): return true
        case .string("false"): return false
        default: throw ToolError("„\(key)“ muss true oder false sein.")
        }
    }

    func stringArray(_ key: String) throws(ToolError) -> [String]? {
        switch values[key] {
        case nil, .null: return nil
        case .array(let items):
            return try items.map { item throws(ToolError) -> String in
                guard case .string(let s) = item else { throw ToolError("„\(key)“ muss eine Liste von Texten sein.") }
                return s
            }
        case .string(let s): return [s]
        default: throw ToolError("„\(key)“ muss eine Liste von Texten sein.")
        }
    }

    func objectArray(_ key: String) throws(ToolError) -> [[String: Value]]? {
        switch values[key] {
        case nil, .null: return nil
        case .array(let items):
            return try items.map { item throws(ToolError) -> [String: Value] in
                guard case .object(let object) = item else { throw ToolError("„\(key)“ muss eine Liste von Objekten sein.") }
                return object
            }
        default: throw ToolError("„\(key)“ muss eine Liste von Objekten sein.")
        }
    }

    /// Tagesbeginn eines Datums „JJJJ-MM-TT“ in der Zeitzone des Nutzers (oder ein voller ISO-Zeitpunkt).
    func dayStart(_ key: String) throws(ToolError) -> Date? {
        guard let text = try string(key) else { return nil }
        guard let date = Self.parseDate(text, timeZone: timeZone) else {
            throw ToolError("„\(key)“ muss ein Datum im Format JJJJ-MM-TT sein, z. B. 2026-09-25.")
        }
        return date
    }

    /// Ende eines Tages (exklusiv): „to: 2026-09-25“ schließt den ganzen 25. ein.
    func dayEnd(_ key: String) throws(ToolError) -> Date? {
        guard let text = try string(key) else { return nil }
        guard let date = Self.parseDate(text, timeZone: timeZone) else {
            throw ToolError("„\(key)“ muss ein Datum im Format JJJJ-MM-TT sein, z. B. 2026-09-25.")
        }
        if text.count == 10 {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = timeZone
            return calendar.date(byAdding: .day, value: 1, to: date)
        }
        return date
    }

    /// Zeitmarke „mm:ss“, „h:mm:ss“ oder Sekunden.
    func time(_ key: String) throws(ToolError) -> TimeInterval? {
        switch values[key] {
        case nil, .null: return nil
        case .int(let i): return TimeInterval(i)
        case .double(let d): return d
        case .string(let s):
            if let seconds = TimeInterval(s) { return seconds }
            let parts = s.split(separator: ":").compactMap { Int($0) }
            guard (2 ... 3).contains(parts.count), parts.count == s.split(separator: ":").count else {
                throw ToolError("„\(key)“ muss eine Zeitmarke wie 12:04 oder 1:02:30 sein.")
            }
            return TimeInterval(parts.reduce(0) { $0 * 60 + $1 })
        default: throw ToolError("„\(key)“ muss eine Zeitmarke wie 12:04 sein.")
        }
    }

    static func parseDate(_ text: String, timeZone: TimeZone) -> Date? {
        if text.count == 10 {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = timeZone
            formatter.dateFormat = "yyyy-MM-dd"
            return formatter.date(from: text)
        }
        let iso = ISO8601DateFormatter()
        return iso.date(from: text)
    }
}
