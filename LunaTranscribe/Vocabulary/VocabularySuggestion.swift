import Foundation

/// Ein aus einer Korrektur gelernter Wörterbuch-Vorschlag: `variant` wurde erkannt, `term` war gemeint.
/// Wird erst aktiv, wenn der Nutzer ihn übernimmt – falsche Ersetzungen sind schlimmer als fehlende.
struct VocabularySuggestion: Codable, Identifiable, Equatable, Sendable {
    var id = UUID()
    /// Richtige Schreibweise („weclapp“).
    var term: String
    /// So hat das Modell den Begriff erkannt („Weglap“).
    var variant: String
    /// Wie oft dieselbe Korrektur gesehen wurde; sortiert die Liste.
    var count = 1
    var lastSeen = Date()

    /// Identität unabhängig von Groß-/Kleinschreibung – dient auch der Ignorierliste.
    var key: String { Self.key(term: term, variant: variant) }

    static func key(term: String, variant: String) -> String {
        "\(variant.lowercased())→\(term.lowercased())"
    }

    /// „1 neuer Wörterbuch-Vorschlag“ / „3 neue Wörterbuch-Vorschläge“
    static func countLabel(_ count: Int) -> String {
        count == 1 ? "1 neuer Wörterbuch-Vorschlag" : "\(count) neue Wörterbuch-Vorschläge"
    }
}
