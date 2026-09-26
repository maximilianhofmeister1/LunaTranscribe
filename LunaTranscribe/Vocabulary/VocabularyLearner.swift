import Foundation
import LunaCore

/// Das lernende Wörterbuch: macht aus Korrekturen im Verlauf und in Meeting-Transkripten Vorschläge
/// und übernimmt sie auf Wunsch. Nichts wird automatisch aktiv – eine falsche Ersetzung wäre in jedem
/// künftigen Diktat, eine fehlende nur ein Tippfehler.
@MainActor
final class VocabularyLearner {
    private let settings: SettingsStore

    init(settings: SettingsStore) {
        self.settings = settings
    }

    var pendingCount: Int { settings.vocabularySuggestions.count }

    /// Vergleicht Original und Korrektur und ergänzt neue Vorschläge. Gibt die Anzahl neuer oder
    /// bestätigter Vorschläge zurück (für einen Hinweis in der UI).
    @discardableResult
    func learn(before: String, after: String) -> Int {
        let known = CustomVocabulary(entries: settings.vocabularyEntries)
        var suggestions = settings.vocabularySuggestions
        var changed = 0

        for candidate in CorrectionLearner.candidates(before: before, after: after) {
            let key = VocabularySuggestion.key(term: candidate.term, variant: candidate.variant)
            guard !settings.ignoredSuggestionKeys.contains(key),
                  known.correct(candidate.variant) != candidate.term else { continue }   // kennt das Wörterbuch schon

            if let index = suggestions.firstIndex(where: { $0.key == key }) {
                suggestions[index].count += 1
                suggestions[index].lastSeen = Date()
            } else {
                suggestions.append(VocabularySuggestion(term: candidate.term, variant: candidate.variant))
            }
            changed += 1
        }
        guard changed > 0 else { return 0 }
        settings.vocabularySuggestions = suggestions.sorted { ($0.count, $0.lastSeen) > ($1.count, $1.lastSeen) }
        Log.vocabulary.notice("learned \(changed) suggestion(s) from a correction")
        return changed
    }

    /// Übernimmt einen Vorschlag: als Variante eines vorhandenen Begriffs oder als neuer Eintrag.
    func accept(_ suggestion: VocabularySuggestion) {
        var entries = settings.vocabularyEntries
        if let index = entries.firstIndex(where: { $0.term.caseInsensitiveCompare(suggestion.term) == .orderedSame }) {
            let hasVariant = entries[index].aliases.contains { $0.caseInsensitiveCompare(suggestion.variant) == .orderedSame }
            if !hasVariant { entries[index].aliases.append(suggestion.variant) }
        } else {
            entries.append(VocabularyEntry(term: suggestion.term, aliases: [suggestion.variant]))
        }
        settings.vocabularyEntries = entries
        remove(suggestion)
    }

    /// Verwirft einen Vorschlag dauerhaft.
    func ignore(_ suggestion: VocabularySuggestion) {
        settings.ignoredSuggestionKeys.insert(suggestion.key)
        remove(suggestion)
    }

    func update(_ suggestion: VocabularySuggestion) {
        guard let index = settings.vocabularySuggestions.firstIndex(where: { $0.id == suggestion.id }) else { return }
        settings.vocabularySuggestions[index] = suggestion
    }

    private func remove(_ suggestion: VocabularySuggestion) {
        settings.vocabularySuggestions.removeAll { $0.id == suggestion.id }
    }
}
