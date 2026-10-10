import os

/// Alle Logger der App an einer Stelle. Lesen mit:
/// `log stream --predicate 'subsystem == "at.aljoma.lunatranscribe"'`
enum Log {
    static let subsystem = "at.aljoma.lunatranscribe"

    static let timing = Logger(subsystem: subsystem, category: "timing")
    static let meeting = Logger(subsystem: subsystem, category: "meeting")
    static let systemAudio = Logger(subsystem: subsystem, category: "systemaudio")
    static let refine = Logger(subsystem: subsystem, category: "refine")
    static let vocabulary = Logger(subsystem: subsystem, category: "vocabulary")
    static let summary = Logger(subsystem: subsystem, category: "summary")
    /// Suchindex und Embedding-Modell (luna-mcp loggt unter der Kategorie „mcp“).
    static let search = Logger(subsystem: subsystem, category: "search")
}
