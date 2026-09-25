import Foundation

/// Tageszähler für die Übersicht: nur Datum und Zahlen, nie Text. Unabhängig vom Verlauf – zählt also
/// auch mit ausgeschaltetem Verlauf, übersteht Neustarts und beginnt jeden Tag bei null.
struct DailyDictationStats: Codable, Equatable {
    /// Kalendertag im Format yyyy-MM-dd (lokale Zeitzone).
    var day: String
    var words = 0
    var dictations = 0

    static let empty = DailyDictationStats(day: "")

    /// Stand für `date`: der gespeicherte Zähler, falls er von diesem Tag ist, sonst null.
    func on(_ date: Date = Date()) -> DailyDictationStats {
        let today = Self.dayKey(for: date)
        return day == today ? self : DailyDictationStats(day: today)
    }

    /// Zählt ein eingefügtes Diktat dazu (setzt an einem neuen Tag vorher zurück).
    func adding(words: Int, on date: Date = Date()) -> DailyDictationStats {
        var stats = on(date)
        stats.words += words
        stats.dictations += 1
        return stats
    }

    static func dayKey(for date: Date) -> String {
        let parts = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0)
    }
}
