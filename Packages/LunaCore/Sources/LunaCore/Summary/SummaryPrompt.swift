import Foundation

/// Anweisung für die Meeting-Zusammenfassung. Wie `RefinePrompt`: ein Standard, den der Nutzer
/// in den Einstellungen anpassen kann; `{language}` wird durch die Meeting-Sprache ersetzt.
/// Liegt im gemeinsamen Kern, weil auch der MCP-Prompt `meeting_summary` dieselbe Fassung nutzt.
///
/// Die Abschnittsüberschriften stehen im Standard auf Deutsch. Nur mit der Bitte, sie zu übersetzen, schrieb
/// das Modell sie im Test (2026-11-12, Claude CLI 2.1.322, Windows-Version mit derselben Anweisung) bei Englisch
/// als Zielsprache etwa jedes zweite Mal trotzdem auf Deutsch. Deshalb ersetzt `instructions` sie für die Sprachen
/// aus `headings(for:)` direkt und nennt sie im Schlusssatz; für alle anderen Sprachen kommt ein ausdrücklicher
/// Übersetzungs-Schlusssatz dazu (getestet: Englisch 5/5, Französisch, Tschechisch, Ungarisch, Japanisch).
public enum SummaryPrompt {
    public static let placeholder = "{language}"

    public static let defaultTemplate = """
    Du bekommst das Transkript eines Meetings. Es wurde automatisch per Spracherkennung erstellt und \
    kann Hörfehler enthalten. Sprecher sind mit Namen oder als „Sprecher 1“, „Sprecher 2“ … gekennzeichnet, \
    jeder Beitrag beginnt mit einem Zeitstempel.

    Schreibe eine Zusammenfassung auf {language} in Markdown mit genau diesen Abschnitten (Überschriften auf {language}):

    ## Kurzfassung
    Drei bis fünf Sätze: Worum ging es, was ist das Ergebnis?

    ## Themen
    Jedes besprochene Thema als fetter Stichpunkt („- **Thema**“), darunter die wichtigsten Aussagen \
    als um zwei Leerzeichen eingerückte Unterpunkte.

    ## Entscheidungen
    Was wurde beschlossen? Schreibe „Keine“, wenn nichts beschlossen wurde.

    ## Aufgaben
    Wer macht was bis wann, als Liste im Format „- [ ] Person: Aufgabe (Frist)“. \
    Lass die Frist weg, wenn keine genannt wurde.

    ## Offene Fragen
    Was blieb ungeklärt? Schreibe „Keine“, wenn alles geklärt ist.

    Regeln: Erfinde nichts, was nicht im Transkript steht. Nenne Personen so, wie sie im Transkript heißen. \
    Schreibe alles auf {language}, auch die Abschnittsüberschriften – selbst wenn das Meeting in einer \
    anderen Sprache geführt wurde. Antworte nur mit der Zusammenfassung, ohne Einleitung oder Schlusssatz.
    """

    /// Fertige Anweisung; `language` ist die Zielsprache der Zusammenfassung (siehe `SummaryLanguage`).
    /// Ein leerer Text fällt auf den Standard zurück.
    public static func instructions(template: String, language: AppLanguage) -> String {
        let trimmed = template.trimmingCharacters(in: .whitespacesAndNewlines)
        var base = trimmed.isEmpty ? defaultTemplate : template
        // Nur ganze Überschriftszeilen zählen – eigene Anweisungen mit anderen Überschriften bleiben unberührt.
        if language != .german, germanHeadings.contains(where: { base.contains("\n## \($0)\n") }) {
            if let localized = headings(for: language) {
                for (german, translated) in zip(germanHeadings, localized) {
                    base = base.replacingOccurrences(of: "\n## \(german)\n", with: "\n## \(translated)\n")
                }
                let list = localized.map { "„## \($0)“" }.joined(separator: ", ")
                base = base.trimmingCharacters(in: .whitespacesAndNewlines) + "\n\nVerwende genau diese Überschriften: \(list)."
            } else {
                base = base.trimmingCharacters(in: .whitespacesAndNewlines)
                    + "\n\nDie Abschnittsüberschriften oben stehen auf Deutsch – übersetze sie auf \(placeholder)."
            }
        }
        return base.replacingOccurrences(of: placeholder, with: languageName(language))
    }

    private static let germanHeadings = ["Kurzfassung", "Themen", "Entscheidungen", "Aufgaben", "Offene Fragen"]

    /// Die fünf Standard-Überschriften in der Zielsprache, in der Reihenfolge von `defaultTemplate`; nil für
    /// Sprachen ohne geprüfte Übersetzung (dort übersetzt das Modell selbst). Gleich mit der Windows-Version.
    public static func headings(for language: AppLanguage) -> [String]? {
        switch language {
        case .german: return germanHeadings
        case .english: return ["Summary", "Topics", "Decisions", "Action Items", "Open Questions"]
        case .french: return ["Résumé", "Thèmes", "Décisions", "Tâches", "Questions ouvertes"]
        case .spanish: return ["Resumen", "Temas", "Decisiones", "Tareas", "Preguntas abiertas"]
        case .italian: return ["Sintesi", "Argomenti", "Decisioni", "Attività", "Domande aperte"]
        case .portuguese: return ["Resumo", "Tópicos", "Decisões", "Tarefas", "Perguntas em aberto"]
        case .dutch: return ["Samenvatting", "Onderwerpen", "Besluiten", "Taken", "Open vragen"]
        case .polish: return ["Podsumowanie", "Tematy", "Decyzje", "Zadania", "Otwarte pytania"]
        case .swedish: return ["Sammanfattning", "Ämnen", "Beslut", "Uppgifter", "Öppna frågor"]
        case .danish: return ["Resumé", "Emner", "Beslutninger", "Opgaver", "Åbne spørgsmål"]
        default: return nil
        }
    }

    /// Sprachname auf Deutsch („Deutsch“, „Englisch“), passend zum deutschen Standard-Prompt.
    public static func languageName(_ language: AppLanguage) -> String {
        Locale(identifier: "de").localizedString(forLanguageCode: language.rawValue) ?? language.rawValue
    }
}
