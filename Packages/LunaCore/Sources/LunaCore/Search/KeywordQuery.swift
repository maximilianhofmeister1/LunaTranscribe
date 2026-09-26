import Foundation

/// Übersetzt eine Suchanfrage in einen FTS5-Ausdruck.
///
/// - Wörter ab 4 Zeichen suchen als Präfix („Preis“ findet „Preise“, „Preiserhöhung“) – hilft bei deutschen
///   Komposita und Beugungen, die der Tokenizer nicht zurückführt.
/// - Text in Anführungszeichen ist eine Phrase.
/// - Füllwörter („wo“, „haben“, „wir“, „über“) fallen weg: Claude formuliert oft ganze Fragen.
/// - Enthält die Anfrage einen Wörterbuch-Begriff oder eine seiner Fehlschreibungen, wird nach allen Varianten
///   gesucht – ältere Transkripte enthalten oft noch die Fehlschreibung.
/// - `all` verlangt jeden Bestandteil (UND); `any` ist die Rückfallebene (ODER), wenn UND zu wenig findet.
public struct KeywordQuery: Equatable, Sendable {
    public let all: String?
    public let any: String?
    /// Normalisierte Suchwörter (für Titel- und Sprechernamen-Treffer außerhalb des Index).
    public let terms: [String]

    public var isEmpty: Bool { all == nil }

    public init(_ text: String, vocabulary: [VocabularyEntry] = []) {
        var remaining = TextFolding.fold(text)
        var clauses: [String] = []
        var terms: [String] = []

        // 1. Wörterbuch-Varianten (längste zuerst), damit „Luna Transcribe“ vor „Luna“ greift.
        for group in Self.variantGroups(vocabulary) {
            guard let match = group.lazy.compactMap({ Self.range(of: $0, in: remaining) }).first else { continue }
            remaining.replaceSubrange(match, with: " ")
            clauses.append("(" + group.map(Self.phrase).joined(separator: " OR ") + ")")
            terms.append(contentsOf: group)
        }

        // 2. Phrasen in Anführungszeichen.
        for quote in ["\"", "„", "“", "”"] { remaining = remaining.replacingOccurrences(of: quote, with: "\"") }
        let quoted = remaining.split(separator: "\"", omittingEmptySubsequences: false)
        var bare: [Substring] = []
        for (index, part) in quoted.enumerated() {
            if index.isMultiple(of: 2) {
                bare.append(part)
            } else {
                let words = Self.words(in: part)
                guard !words.isEmpty else { continue }
                clauses.append(Self.phrase(words.joined(separator: " ")))
                terms.append(words.joined(separator: " "))
            }
        }

        // 3. Einzelwörter ohne Füllwörter.
        for word in bare.flatMap(Self.words) where word.count > 1 && !Self.stopWords.contains(word) {
            clauses.append(word.count >= 4 ? Self.phrase(word) + "*" : Self.phrase(word))
            terms.append(word)
        }

        self.all = clauses.isEmpty ? nil : clauses.joined(separator: " AND ")
        self.any = clauses.count > 1 ? clauses.joined(separator: " OR ") : nil
        self.terms = terms
    }

    // MARK: Hilfen

    private static func phrase(_ text: String) -> String {
        "\"" + text.replacingOccurrences(of: "\"", with: "") + "\""
    }

    private static func words(in text: Substring) -> [String] {
        text.split(whereSeparator: { !($0.isLetter || $0.isNumber) }).map(String.init)
    }

    /// Je Wörterbuch-Eintrag alle Schreibweisen (gefaltet), Gruppen mit den längsten Varianten zuerst.
    private static func variantGroups(_ vocabulary: [VocabularyEntry]) -> [[String]] {
        vocabulary.compactMap { entry -> [String]? in
            let variants = ([entry.term] + entry.aliases)
                .map { TextFolding.fold($0).split(whereSeparator: \.isWhitespace).joined(separator: " ") }
                .filter { !$0.isEmpty }
            let unique = variants.reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
            return unique.isEmpty ? nil : unique.sorted { $0.count > $1.count }
        }
        .sorted { ($0.first?.count ?? 0) > ($1.first?.count ?? 0) }
    }

    /// Fundstelle einer Variante an Wortgrenzen (Leerzeichen flexibel).
    private static func range(of variant: String, in text: String) -> Range<String.Index>? {
        let pattern = variant.split(separator: " ").map { NSRegularExpression.escapedPattern(for: String($0)) }
            .joined(separator: "\\s+")
        let wordChar = "[\\p{L}\\p{N}_]"
        return text.range(of: "(?<!\(wordChar))\(pattern)(?!\(wordChar))", options: .regularExpression)
    }

    /// Häufige deutsche und englische Wörter ohne Suchwert.
    static let stopWords: Set<String> = [
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "eines",
        "und", "oder", "aber", "doch", "sondern", "denn", "weil", "dass", "ob", "wenn", "als", "wie",
        "ich", "du", "er", "sie", "es", "wir", "ihr", "mich", "mir", "dich", "dir", "uns", "euch", "ihnen",
        "mein", "dein", "sein", "unser", "euer", "ihre", "ihren", "unsere", "unseren",
        "ist", "sind", "war", "waren", "bin", "bist", "seid", "wird", "werden", "wurde", "wurden",
        "hat", "habe", "haben", "hatte", "hatten", "hast", "kann", "konnen", "konnte", "soll", "sollen",
        "muss", "mussen", "will", "wollen", "gibt", "geben", "gab", "machen", "macht", "gemacht",
        "in", "im", "an", "am", "auf", "aus", "bei", "mit", "nach", "von", "vom", "zu", "zum", "zur",
        "uber", "unter", "vor", "hinter", "neben", "zwischen", "durch", "fur", "gegen", "ohne", "um", "bis", "seit",
        "nicht", "kein", "keine", "auch", "noch", "schon", "nur", "sehr", "mal", "ja", "nein", "so", "da", "dann",
        "was", "wer", "wo", "wann", "warum", "wieso", "welche", "welcher", "welches", "worum", "woruber",
        "alle", "allen", "alles", "etwas", "man", "sich", "hier", "dort", "heute", "gestern",
        "gesagt", "gesprochen", "besprochen", "diskutiert", "meeting", "meetings",
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "with", "about", "is", "are", "was", "were",
        "what", "who", "where", "when", "why", "how", "did", "do", "does", "we", "you", "they", "it", "that", "this",
    ]
}

/// Einheitliche Normalisierung für Suche und Vergleiche: klein, ohne Akzente/Umlaut-Punkte.
public enum TextFolding {
    /// Wie der FTS5-Tokenizer (`unicode61 remove_diacritics 2`): Akzente weg, klein – „ß“ bleibt „ß“
    /// (eine Groß-/Kleinschreibungs-Faltung würde „ss“ daraus machen und im Index nichts mehr finden).
    public static func fold(_ text: String) -> String {
        text.folding(options: .diacriticInsensitive, locale: nil).lowercased()
    }

    /// Enthält `text` den gefalteten Suchbegriff `needle` (bereits gefaltet übergeben)?
    public static func contains(_ text: String, folded needle: String) -> Bool {
        fold(text).contains(needle)
    }
}
