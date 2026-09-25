import Foundation

/// Ein Eintrag im eigenen Wörterbuch: die richtige Schreibweise und die Varianten,
/// als die das ASR-Modell den Begriff falsch erkennt.
struct VocabularyEntry: Codable, Identifiable, Equatable, Sendable {
    var id = UUID()
    /// Richtige Schreibweise, landet so im Text („LunaTranscribe“).
    var term: String
    /// Bekannte Fehlerkennungen („Luna Transcribe“, „Luna transkribe“).
    var aliases: [String] = []
}

/// Das aktive Wörterbuch, so wie es eine Engine anwendet. Unveränderlich und threadsicher:
/// Der Regex wird einmal beim Erzeugen gebaut, `correct` läuft auch auf Hintergrund-Tasks (Meeting).
///
/// Zwei Stufen nutzen dieselben Einträge:
/// 1. `correct(_:)` ersetzt jede Variante (und den Begriff in falscher Groß-/Kleinschreibung)
///    deterministisch durch den Begriff – für jede Engine und jede Sprache.
/// 2. Parakeet nutzt Begriffe und Varianten zusätzlich für akustisches Boosting (`ParakeetEngine`).
struct CustomVocabulary: Equatable, @unchecked Sendable {
    /// Bereinigte Einträge: Begriff nicht leer, Varianten getrimmt und ohne Leereinträge.
    let entries: [VocabularyEntry]

    /// Findet alle Varianten in einem Durchlauf; nil bei leerem Wörterbuch.
    private let pattern: NSRegularExpression?
    /// Normalisierte Variante (klein, einfache Leerzeichen) → Begriff.
    private let replacements: [String: String]

    static let disabled = CustomVocabulary(entries: [])

    var isEmpty: Bool { entries.isEmpty }

    init(entries: [VocabularyEntry]) {
        let cleaned: [VocabularyEntry] = entries.compactMap { entry in
            let term = entry.term.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !term.isEmpty else { return nil }
            let aliases = entry.aliases
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
            return VocabularyEntry(id: entry.id, term: term, aliases: aliases)
        }
        self.entries = cleaned

        // Der Begriff selbst zählt als Variante, damit auch „kubernetes“ → „Kubernetes“ korrigiert wird.
        // Bei doppelten Varianten gewinnt der erste Eintrag.
        var replacements: [String: String] = [:]
        for entry in cleaned {
            for variant in [entry.term] + entry.aliases {
                let key = Self.normalize(variant)
                if replacements[key] == nil { replacements[key] = entry.term }
            }
        }
        self.replacements = replacements
        self.pattern = Self.makePattern(for: Array(replacements.keys))
    }

    /// Ersetzt alle bekannten Varianten durch die richtige Schreibweise. Groß-/Kleinschreibung und
    /// die Anzahl der Leerzeichen zwischen Wörtern spielen keine Rolle; ersetzt wird nur an
    /// Wortgrenzen (Unicode-fähig, also auch bei Umlauten), nie mitten in einem Wort.
    func correct(_ text: String) -> String {
        guard let pattern else { return text }
        let matches = pattern.matches(in: text, range: NSRange(text.startIndex..., in: text))
        guard !matches.isEmpty else { return text }

        var result = text
        // Von hinten ersetzen, damit die vorderen Bereiche gültig bleiben.
        for match in matches.reversed() {
            guard let range = Range(match.range, in: result),
                  let term = replacements[Self.normalize(String(result[range]))] else { continue }
            result.replaceSubrange(range, with: term)
        }
        return result
    }

    static func == (lhs: CustomVocabulary, rhs: CustomVocabulary) -> Bool {
        lhs.entries == rhs.entries
    }

    // MARK: Hilfen

    private static func normalize(_ text: String) -> String {
        text.lowercased().split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    /// Eine Alternation über alle Varianten, längste zuerst: So gewinnt „Luna Transcribe“ vor
    /// „Luna“, und ein bereits ersetzter Begriff wird nie ein zweites Mal ersetzt.
    private static func makePattern(for variants: [String]) -> NSRegularExpression? {
        guard !variants.isEmpty else { return nil }
        let alternatives = variants
            .sorted { $0.count > $1.count }
            .map { variant in
                variant.split(separator: " ")
                    .map { NSRegularExpression.escapedPattern(for: String($0)) }
                    .joined(separator: "\\s+")
            }
            .joined(separator: "|")
        // Wortgrenze über Lookarounds statt \b: \b versagt bei Varianten, die mit einem
        // Sonderzeichen beginnen oder enden („C++“, „.NET“).
        let wordChar = "[\\p{L}\\p{N}_]"
        return try? NSRegularExpression(pattern: "(?<!\(wordChar))(?:\(alternatives))(?!\(wordChar))",
                                        options: [.caseInsensitive])
    }
}
