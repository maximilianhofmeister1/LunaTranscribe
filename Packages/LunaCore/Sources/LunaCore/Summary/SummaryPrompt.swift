import Foundation

/// Anweisung für die Meeting-Zusammenfassung. Wie `RefinePrompt`: ein Standard, den der Nutzer
/// in den Einstellungen anpassen kann; `{language}` wird durch die Meeting-Sprache ersetzt.
/// Liegt im gemeinsamen Kern, weil auch der MCP-Prompt `meeting_summary` dieselbe Fassung nutzt.
public enum SummaryPrompt {
    public static let placeholder = "{language}"

    public static let defaultTemplate = """
    Du bekommst das Transkript eines Meetings. Es wurde automatisch per Spracherkennung erstellt und \
    kann Hörfehler enthalten. Sprecher sind mit Namen oder als „Sprecher 1“, „Sprecher 2“ … gekennzeichnet, \
    jeder Beitrag beginnt mit einem Zeitstempel.

    Schreibe eine Zusammenfassung auf {language} in Markdown mit genau diesen Abschnitten:

    ## Kurzfassung
    Drei bis fünf Sätze: Worum ging es, was ist das Ergebnis?

    ## Themen
    Die besprochenen Themen als Stichpunkte, jeweils mit den wichtigsten Aussagen.

    ## Entscheidungen
    Was wurde beschlossen? Schreibe „Keine“, wenn nichts beschlossen wurde.

    ## Aufgaben
    Wer macht was bis wann, als Liste im Format „- [ ] Person: Aufgabe (Frist)“. \
    Lass die Frist weg, wenn keine genannt wurde.

    ## Offene Fragen
    Was blieb ungeklärt? Schreibe „Keine“, wenn alles geklärt ist.

    Regeln: Erfinde nichts, was nicht im Transkript steht. Nenne Personen so, wie sie im Transkript heißen. \
    Antworte nur mit der Zusammenfassung, ohne Einleitung oder Schlusssatz.
    """

    /// Fertige Anweisung für ein Meeting; ein leerer Text fällt auf den Standard zurück.
    public static func instructions(template: String, language: AppLanguage) -> String {
        let trimmed = template.trimmingCharacters(in: .whitespacesAndNewlines)
        let base = trimmed.isEmpty ? defaultTemplate : template
        return base.replacingOccurrences(of: placeholder, with: languageName(language))
    }

    /// Sprachname auf Deutsch („Deutsch“, „Englisch“), passend zum deutschen Standard-Prompt.
    public static func languageName(_ language: AppLanguage) -> String {
        Locale(identifier: "de").localizedString(forLanguageCode: language.rawValue) ?? language.rawValue
    }
}
