import Foundation

/// Erkennt in einer Nutzerkorrektur („vorher“ → „nachher“) Verhörer, die ins Wörterbuch gehören.
///
/// Vorgehen: Wort-Diff über die längste gemeinsame Teilfolge; jede Stelle, an der 1–3 Wörter durch
/// 1–3 andere ersetzt wurden, ist ein Kandidat. Verworfen wird, was nach Umformulierung statt Verhörer
/// aussieht: Füllwörter, reine Satzanfang-Großschreibung, zu unähnliche Schreibweisen.
/// Rein und ohne Zustand – testbar über `--learn-selftest`.
enum CorrectionLearner {
    struct Candidate: Equatable {
        /// So stand es da (Erkennung).
        let variant: String
        /// So hat der Nutzer es korrigiert.
        let term: String
    }

    static let maxWords = 3
    /// Anteil unveränderter Wörter, ab dem eine Änderung als Korrektur gilt. Darunter wurde der Text
    /// umgeschrieben – dann sind auch zufällig ähnliche Wörter keine Verhörer.
    static let minKeptRatio = 0.5
    /// Mindestähnlichkeit (0…1) der Schreibweise oder des Klangs, damit es als Verhörer zählt.
    static let minSimilarity = 0.5

    static func candidates(before: String, after: String) -> [Candidate] {
        let old = tokens(before), new = tokens(after)
        guard !old.isEmpty, !new.isEmpty else { return [] }
        let kept = commonWordCount(old.map(\.word), new.map(\.word))
        guard Double(kept) / Double(max(old.count, new.count)) >= minKeptRatio else { return [] }

        var result: [Candidate] = []
        for hunk in replacements(old.map(\.word), new.map(\.word)) {
            let variant = hunk.removed.joined(separator: " ")
            let term = hunk.inserted.joined(separator: " ")
            let atSentenceStart = hunk.oldIndex == 0 || old[hunk.oldIndex - 1].endsSentence
            if isLikelyMishearing(variant: variant, term: term, atSentenceStart: atSentenceStart),
               !result.contains(Candidate(variant: variant, term: term)) {
                result.append(Candidate(variant: variant, term: term))
            }
        }
        return result
    }

    // MARK: Filter

    static func isLikelyMishearing(variant: String, term: String, atSentenceStart: Bool) -> Bool {
        guard variant != term, term.count >= 2, variant.count >= 2 else { return false }
        guard term.rangeOfCharacter(from: .letters) != nil else { return false }   // Zahlen, Zeichen

        let variantWords = variant.lowercased().split(separator: " ").map(String.init)
        let termWords = term.lowercased().split(separator: " ").map(String.init)
        if termWords.allSatisfy(stopwords.contains) || variantWords.allSatisfy(stopwords.contains) { return false }

        // Nur die Groß-/Kleinschreibung geändert: am Satzanfang ist das Grammatik, sonst eine Marke („weclapp“, „iPhone“).
        if variant.lowercased() == term.lowercased() { return !atSentenceStart }
        if isInflection(variant: variant, term: term) { return false }

        let spelling = similarity(compact(variant), compact(term))
        let sound = similarity(phoneticKey(variant), phoneticKey(term))
        return max(spelling, sound) >= minSimilarity
    }

    /// Häufige Funktionswörter (de/en): deren Austausch ist Umformulierung, kein Verhörer.
    static let stopwords: Set<String> = [
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "aber",
        "ich", "du", "er", "sie", "es", "wir", "ihr", "mit", "von", "zu", "zum", "zur", "im", "in", "an", "am", "auf",
        "für", "ist", "sind", "war", "hat", "haben", "nicht", "noch", "auch", "so", "dass", "da", "wie", "was", "wo",
        "the", "a", "an", "and", "or", "but", "i", "you", "he", "she", "it", "we", "they", "to", "of", "in", "on", "at",
        "for", "is", "are", "was", "be", "not", "that", "this", "with", "as", "so",
    ]

    /// Grammatik statt Verhörer: ein normales, kleingeschriebenes Wort mit gleichem Stamm und anderer
    /// Endung („klappen“ → „klappten“, „gehe“ → „gehen“). Namen und Marken tragen fast immer Großbuchstaben.
    static func isInflection(variant: String, term: String) -> Bool {
        guard !variant.contains(" "), !term.contains(" "),
              variant == variant.lowercased(), term == term.lowercased() else { return false }
        let shared = zip(variant, term).prefix { $0 == $1 }.count
        return shared >= 4 && Double(shared) >= 0.6 * Double(min(variant.count, term.count))
    }

    // MARK: Ähnlichkeit

    /// Normalisierte Levenshtein-Ähnlichkeit: 1 = gleich, 0 = völlig verschieden.
    static func similarity(_ a: String, _ b: String) -> Double {
        let a = Array(a), b = Array(b)
        guard !a.isEmpty else { return b.isEmpty ? 1 : 0 }
        guard !b.isEmpty else { return 0 }
        var previous = Array(0...b.count)
        for i in 1...a.count {
            var current = [i] + Array(repeating: 0, count: b.count)
            for j in 1...b.count {
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            previous = current
        }
        return 1 - Double(previous[b.count]) / Double(max(a.count, b.count))
    }

    /// Grobe Klangform für Deutsch/Englisch: gleiche Laute, verschiedene Schreibweisen („Weglap“ ≈ „weclapp“).
    static func phoneticKey(_ text: String) -> String {
        var s = compact(text)
        for (from, to) in [("sch", "s"), ("ph", "f"), ("th", "t"), ("ck", "k"), ("qu", "kv"), ("dt", "t"),
                           ("ie", "i"), ("ei", "ai"), ("ey", "ai"), ("tz", "z"), ("ä", "e"), ("ö", "o"), ("ü", "u"),
                           ("w", "v"), ("c", "k"), ("g", "k"), ("b", "p"), ("d", "t"), ("z", "s"), ("y", "i")] {
            s = s.replacingOccurrences(of: from, with: to)
        }
        var result = ""
        for ch in s where ch != "h" && result.last != ch { result.append(ch) }
        return result
    }

    /// Klein, ohne Leer- und Satzzeichen.
    private static func compact(_ text: String) -> String {
        String(text.lowercased().filter { $0.isLetter || $0.isNumber })
    }

    // MARK: Diff

    struct Token {
        /// Ohne Satzzeichen am Rand – verglichen wird nur das Wort.
        let word: String
        let endsSentence: Bool
    }

    static func tokens(_ text: String) -> [Token] {
        text.split(whereSeparator: \.isWhitespace).compactMap { raw in
            let word = raw.trimmingCharacters(in: .punctuationCharacters.union(.symbols).subtracting(CharacterSet(charactersIn: "+#")))
            guard !word.isEmpty else { return nil }
            let last = raw.last.map(String.init) ?? ""
            return Token(word: word, endsSentence: [".", "!", "?", ":"].contains(last))
        }
    }

    struct Hunk {
        let oldIndex: Int
        let removed: [String]
        let inserted: [String]
    }

    /// LCS-Tabelle von hinten: `lcs[i][j]` = gemeinsame Wörter von `a[i...]` und `b[j...]`.
    private static func lcsTable(_ a: [String], _ b: [String]) -> [[Int]] {
        var lcs = Array(repeating: Array(repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in stride(from: a.count - 1, through: 0, by: -1) {
            for j in stride(from: b.count - 1, through: 0, by: -1) {
                lcs[i][j] = a[i] == b[j] ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
            }
        }
        return lcs
    }

    static func commonWordCount(_ a: [String], _ b: [String]) -> Int { lcsTable(a, b)[0][0] }

    /// Ersetzungen (nicht reine Einfügungen/Löschungen) aus dem LCS-Diff zweier Wortfolgen.
    static func replacements(_ a: [String], _ b: [String]) -> [Hunk] {
        let lcs = lcsTable(a, b)

        var hunks: [Hunk] = []
        var i = 0, j = 0
        var removed: [String] = [], inserted: [String] = [], start = 0
        func beginHunkIfNeeded() { if removed.isEmpty && inserted.isEmpty { start = i } }
        func flush() {
            if !removed.isEmpty, !inserted.isEmpty, removed.count <= maxWords, inserted.count <= maxWords {
                hunks.append(Hunk(oldIndex: start, removed: removed, inserted: inserted))
            }
            removed = []
            inserted = []
        }
        while i < a.count || j < b.count {
            if i < a.count, j < b.count, a[i] == b[j] {
                flush()
                i += 1
                j += 1
            } else if j < b.count, i == a.count || lcs[i][j + 1] >= lcs[i + 1][j] {
                beginHunkIfNeeded()
                inserted.append(b[j])
                j += 1
            } else {
                beginHunkIfNeeded()
                removed.append(a[i])
                i += 1
            }
        }
        flush()
        return hunks
    }
}
