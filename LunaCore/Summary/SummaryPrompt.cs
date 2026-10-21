namespace LunaCore;

/// <summary>
/// Anweisung für die Meeting-Zusammenfassung. Ein Standard, den der Nutzer in den Einstellungen anpassen kann;
/// <c>{language}</c> wird durch die Meeting-Sprache ersetzt. Liegt im gemeinsamen Kern, weil auch der MCP-Prompt
/// <c>meeting_summary</c> dieselbe Fassung nutzt. Wortgleich mit der macOS-Version.
/// </summary>
public static class SummaryPrompt
{
    public const string Placeholder = "{language}";

    // Zeilen mit "\n" verbunden statt als mehrzeiliges Literal, damit CRLF im Quelltext nichts ändert.
    public static readonly string DefaultTemplate = string.Join("\n",
        "Du bekommst das Transkript eines Meetings. Es wurde automatisch per Spracherkennung erstellt und "
        + "kann Hörfehler enthalten. Sprecher sind mit Namen oder als „Sprecher 1“, „Sprecher 2“ … gekennzeichnet, "
        + "jeder Beitrag beginnt mit einem Zeitstempel.",
        "",
        "Schreibe eine Zusammenfassung auf {language} in Markdown mit genau diesen Abschnitten:",
        "",
        "## Kurzfassung",
        "Drei bis fünf Sätze: Worum ging es, was ist das Ergebnis?",
        "",
        "## Themen",
        "Die besprochenen Themen als Stichpunkte, jeweils mit den wichtigsten Aussagen.",
        "",
        "## Entscheidungen",
        "Was wurde beschlossen? Schreibe „Keine“, wenn nichts beschlossen wurde.",
        "",
        "## Aufgaben",
        "Wer macht was bis wann, als Liste im Format „- [ ] Person: Aufgabe (Frist)“. "
        + "Lass die Frist weg, wenn keine genannt wurde.",
        "",
        "## Offene Fragen",
        "Was blieb ungeklärt? Schreibe „Keine“, wenn alles geklärt ist.",
        "",
        "Regeln: Erfinde nichts, was nicht im Transkript steht. Nenne Personen so, wie sie im Transkript heißen. "
        + "Antworte nur mit der Zusammenfassung, ohne Einleitung oder Schlusssatz.");

    /// <summary>Fertige Anweisung für ein Meeting; ein leerer Text fällt auf den Standard zurück.</summary>
    public static string Instructions(string? template, AppLanguage language)
    {
        var trimmed = (template ?? "").Trim();
        var source = trimmed.Length == 0 ? DefaultTemplate : template!;
        return source.Replace(Placeholder, LanguageName(language));
    }

    /// <summary>Sprachname auf Deutsch („Deutsch“, „Englisch“), passend zum deutschen Standard-Prompt.</summary>
    public static string LanguageName(AppLanguage language) => language.DisplayName();
}
