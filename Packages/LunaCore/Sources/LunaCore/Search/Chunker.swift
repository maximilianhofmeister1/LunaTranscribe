import Foundation

/// Ein Suchabschnitt: aufeinanderfolgende Beiträge (oder ein Abschnitt der Zusammenfassung), klein genug
/// für das Embedding-Modell und groß genug, um als Treffer für sich zu stehen.
public struct Chunk: Equatable, Sendable {
    public enum Kind: Int, Sendable {
        case turns = 0
        case summary = 1
    }

    public let kind: Kind
    /// Beitragsbereich (Indizes in `MeetingTranscript.turns`); nil bei der Zusammenfassung.
    public let turns: ClosedRange<Int>?
    public let start: TimeInterval
    public let end: TimeInterval
    /// Sprecher-IDs, keine Namen: Umbenennen macht den Index so nicht ungültig.
    public let speakerIDs: [String]
    public let text: String
}

/// Zerlegt ein Meeting in Suchabschnitte.
///
/// - Beiträge werden bis etwa `targetWords` Wörter zusammengefasst; der letzte Beitrag eines Abschnitts
///   eröffnet den nächsten noch einmal (Überlappung), sofern er kurz ist – so reißt kein Gedanke an der Grenze ab.
/// - Sehr lange Beiträge werden an Satzgrenzen geteilt.
/// - Eingebettet wird nur der gesprochene Text, ohne Sprechernamen.
/// - ~200 Wörter bleiben sicher unter den 512 Token von `multilingual-e5-small`.
public enum Chunker {
    public static let targetWords = 200
    /// Überlappung nur mit Beiträgen bis zu dieser Länge, sonst würden Abschnitte fast doppelt indexiert.
    static let maxOverlapWords = 60
    /// Geht in das Änderungsmerkmal des Index ein: Ändert sich die Zerlegung, wird neu indexiert.
    public static let version = 1

    private struct Piece {
        let turn: Int
        let start: TimeInterval
        let end: TimeInterval
        let speakerID: String
        let text: String
        let words: Int
    }

    public static func chunks(for transcript: MeetingTranscript) -> [Chunk] {
        turnChunks(transcript) + summaryChunks(transcript)
    }

    private static func turnChunks(_ transcript: MeetingTranscript) -> [Chunk] {
        let pieces = transcript.turns.enumerated().flatMap { index, turn in
            split(turn.text).map { text in
                Piece(turn: index, start: turn.start, end: turn.end, speakerID: turn.speakerID,
                      text: text, words: wordCount(text))
            }
        }

        var chunks: [Chunk] = []
        var window: [Piece] = []
        var words = 0
        // Enthält das Fenster mehr als die übernommene Überlappung? Nur dann lohnt ein eigener Abschnitt.
        var hasNewContent = false
        for piece in pieces {
            if hasNewContent, words + piece.words > targetWords {
                chunks.append(makeChunk(window))
                let carry = window.last.flatMap { $0.words <= maxOverlapWords ? $0 : nil }
                window = carry.map { [$0] } ?? []
                words = carry?.words ?? 0
                hasNewContent = false
            }
            window.append(piece)
            words += piece.words
            hasNewContent = true
        }
        if hasNewContent { chunks.append(makeChunk(window)) }
        return chunks
    }

    private static func makeChunk(_ window: [Piece]) -> Chunk {
        var speakers: [String] = []
        for piece in window where !speakers.contains(piece.speakerID) { speakers.append(piece.speakerID) }
        return Chunk(kind: .turns,
                     turns: window.first!.turn ... window.last!.turn,
                     start: window.first!.start,
                     end: window.last!.end,
                     speakerIDs: speakers,
                     text: window.map(\.text).joined(separator: "\n"))
    }

    /// Zusammenfassung nach Überschriften („## Aufgaben“ …) geteilt; jeder Abschnitt ein Treffer.
    private static func summaryChunks(_ transcript: MeetingTranscript) -> [Chunk] {
        guard let markdown = transcript.summary?.markdown, !markdown.isEmpty else { return [] }
        var sections: [[Substring]] = [[]]
        for line in markdown.split(separator: "\n", omittingEmptySubsequences: false) {
            if line.hasPrefix("#"), !(sections.last?.isEmpty ?? true) { sections.append([]) }
            sections[sections.count - 1].append(line)
        }
        return sections
            .map { $0.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .flatMap { split($0) }
            .map { Chunk(kind: .summary, turns: nil, start: 0, end: 0, speakerIDs: [], text: $0) }
    }

    /// Teilt Texte über `targetWords` an Satzenden; kürzere bleiben unverändert.
    static func split(_ text: String) -> [String] {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        guard wordCount(trimmed) > targetWords else { return [trimmed] }

        var sentences: [String] = []
        var current = ""
        for character in trimmed {
            current.append(character)
            if ".!?\n".contains(character) {
                sentences.append(current)
                current = ""
            }
        }
        if !current.isEmpty { sentences.append(current) }

        var parts: [String] = []
        var part = ""
        for sentence in sentences {
            if !part.isEmpty, wordCount(part) + wordCount(sentence) > targetWords {
                parts.append(part.trimmingCharacters(in: .whitespacesAndNewlines))
                part = ""
            }
            part += sentence
        }
        if !part.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            parts.append(part.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        // Ein einzelner „Satz“ ohne Satzzeichen kann länger sein – dann hart nach Wörtern teilen.
        return parts.flatMap { wordCount($0) > targetWords * 2 ? hardSplit($0) : [$0] }
    }

    private static func hardSplit(_ text: String) -> [String] {
        let words = text.split(whereSeparator: \.isWhitespace)
        return stride(from: 0, to: words.count, by: targetWords).map {
            words[$0 ..< min($0 + targetWords, words.count)].joined(separator: " ")
        }
    }

    static func wordCount(_ text: String) -> Int {
        text.split(whereSeparator: \.isWhitespace).count
    }
}
