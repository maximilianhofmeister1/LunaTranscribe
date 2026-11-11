import Foundation

/// Ein Block aus dem Markdown der Zusammenfassungen: Überschrift, Listenpunkt oder Absatz.
/// Bewusst klein – mehr erzeugt der Zusammenfassungs-Prompt nicht. Inline-Formatierung (fett, kursiv)
/// bleibt im Text stehen und ist Sache der Darstellung.
public enum MarkdownBlock: Equatable, Sendable {
    public enum ListMarker: Equatable, Sendable {
        case bullet
        case task(done: Bool)
    }

    case heading(String, level: Int)
    /// `depth` 0 ist die oberste Ebene, jede Unterliste liegt eine Ebene tiefer.
    case listItem(String, marker: ListMarker, depth: Int)
    case paragraph(String)

    /// Zerlegt Markdown zeilenweise. Leerzeilen trennen nur, sie beenden keine Liste – Modelle setzen
    /// sie gern zwischen zwei Oberpunkte. Alles andere als ein Listenpunkt beendet die Liste.
    public static func parse(_ markdown: String) -> [MarkdownBlock] {
        var nesting = ListNesting()
        return markdown.components(separatedBy: .newlines).compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { return nil }
            if let item = listItem(in: line) {
                return .listItem(item.text, marker: item.marker, depth: nesting.depth(forIndent: indentWidth(of: raw)))
            }
            nesting.reset()
            return heading(in: line) ?? .paragraph(line)
        }
    }

    private static let maxHeadingLevel = 6
    private static let bulletPrefixes = ["- ", "* ", "+ ", "• "]
    private static let taskPrefixes = [("[ ] ", false), ("[x] ", true), ("[X] ", true)]
    private static let tabWidth = 4

    private static func heading(in line: String) -> MarkdownBlock? {
        let level = line.prefix { $0 == "#" }.count
        guard (1...maxHeadingLevel).contains(level), line.dropFirst(level).first == " " else { return nil }
        return .heading(line.dropFirst(level).trimmingCharacters(in: .whitespaces), level: level)
    }

    private static func listItem(in line: String) -> (text: String, marker: ListMarker)? {
        guard let bullet = bulletPrefixes.first(where: line.hasPrefix) else { return nil }
        let text = line.dropFirst(bullet.count)
        for (prefix, done) in taskPrefixes where text.hasPrefix(prefix) {
            return (String(text.dropFirst(prefix.count)), .task(done: done))
        }
        return (String(text), .bullet)
    }

    private static func indentWidth(of line: String) -> Int {
        line.prefix { $0 == " " || $0 == "\t" }.reduce(0) { $0 + ($1 == "\t" ? tabWidth : 1) }
    }
}

/// Leitet die Listentiefe aus der Einrückung ab, ohne eine feste Schrittweite anzunehmen: Modelle
/// rücken mal um zwei, mal um vier Zeichen ein. Wie bei CommonMark zählt die Einrückung relativ zum
/// Elternpunkt, und erst ab zwei Zeichen ist ein Punkt dessen Kind (eine einzelne Leerstelle ist Zufall).
private struct ListNesting {
    private static let minimumStep = 2

    /// Einrückung der offenen Ebenen, von außen nach innen.
    private var indents: [Int] = []

    mutating func depth(forIndent indent: Int) -> Int {
        while let innermost = indents.last, innermost > indent { indents.removeLast() }
        if let innermost = indents.last, indent < innermost + Self.minimumStep {
            return indents.count - 1   // Geschwister des innersten offenen Punkts
        }
        indents.append(indent)
        return indents.count - 1
    }

    mutating func reset() { indents.removeAll() }
}
