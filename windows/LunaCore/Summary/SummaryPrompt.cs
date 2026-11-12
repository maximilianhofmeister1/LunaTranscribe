using System.Linq;

namespace LunaCore;

/// <summary>
/// Anweisung für die Meeting-Zusammenfassung. Ein Standard, den der Nutzer in den Einstellungen anpassen kann;
/// <c>{language}</c> wird durch die Zielsprache ersetzt. Liegt im gemeinsamen Kern, weil auch der MCP-Prompt
/// <c>meeting_summary</c> dieselbe Fassung nutzt. Bis auf die Überschriften-Zeile wortgleich mit der macOS-Version.
///
/// Die Abschnittsüberschriften stehen im Standard auf Deutsch. Mit der macOS-Fassung schrieb das Modell sie im Test
/// (2026-11-12, Claude CLI 2.1.322) bei Englisch als Zielsprache etwa jedes zweite Mal trotzdem auf Deutsch. Deshalb
/// ersetzt <see cref="Instructions"/> sie für die Sprachen aus <see cref="Headings"/> direkt und hängt die Liste als
/// Schlusssatz an; für alle anderen Sprachen kommt ein ausdrücklicher Übersetzungs-Schlusssatz dazu (getestet:
/// Englisch 5/5, Französisch, Tschechisch, Ungarisch, Japanisch).
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
        "Schreibe eine Zusammenfassung auf {language} in Markdown mit genau diesen Abschnitten (Überschriften auf {language}):",
        "",
        "## Kurzfassung",
        "Drei bis fünf Sätze: Worum ging es, was ist das Ergebnis?",
        "",
        "## Themen",
        "Jedes besprochene Thema als fetter Stichpunkt („- **Thema**“), darunter die wichtigsten Aussagen "
        + "als um zwei Leerzeichen eingerückte Unterpunkte.",
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
        + "Schreibe alles auf {language}, auch die Abschnittsüberschriften – selbst wenn das Meeting in einer "
        + "anderen Sprache geführt wurde. Antworte nur mit der Zusammenfassung, ohne Einleitung oder Schlusssatz.");

    /// <summary>
    /// Fertige Anweisung; <paramref name="language"/> ist die Zielsprache der Zusammenfassung (siehe <see cref="SummaryLanguage"/>).
    /// Ein leerer Text fällt auf den Standard zurück.
    /// </summary>
    public static string Instructions(string? template, AppLanguage language)
    {
        var trimmed = (template ?? "").Trim();
        var source = (trimmed.Length == 0 ? DefaultTemplate : template!).Replace("\r\n", "\n");
        // Nur ganze Überschriftszeilen zählen – eigene Anweisungen mit anderen Überschriften bleiben unberührt.
        if (language != AppLanguage.German && GermanHeadings.Any(h => source.Contains($"\n## {h}\n")))
        {
            if (Headings(language) is { } localized)
            {
                for (var i = 0; i < GermanHeadings.Length; i++)
                    source = source.Replace($"\n## {GermanHeadings[i]}\n", $"\n## {localized[i]}\n");
                source = source.TrimEnd() + "\n\nVerwende genau diese Überschriften: "
                         + string.Join(", ", localized.Select(h => $"„## {h}“")) + ".";
            }
            else
            {
                source = source.TrimEnd() + $"\n\nDie Abschnittsüberschriften oben stehen auf Deutsch – übersetze sie auf {Placeholder}.";
            }
        }
        return source.Replace(Placeholder, LanguageName(language));
    }

    private static readonly string[] GermanHeadings = { "Kurzfassung", "Themen", "Entscheidungen", "Aufgaben", "Offene Fragen" };

    /// <summary>
    /// Die fünf Standard-Überschriften in der Zielsprache, in der Reihenfolge von <see cref="DefaultTemplate"/>; null für
    /// Sprachen ohne geprüfte Übersetzung (dort übersetzt das Modell selbst).
    /// </summary>
    public static string[]? Headings(AppLanguage language) => language switch
    {
        AppLanguage.German => GermanHeadings,
        AppLanguage.English => new[] { "Summary", "Topics", "Decisions", "Action Items", "Open Questions" },
        AppLanguage.French => new[] { "Résumé", "Thèmes", "Décisions", "Tâches", "Questions ouvertes" },
        AppLanguage.Spanish => new[] { "Resumen", "Temas", "Decisiones", "Tareas", "Preguntas abiertas" },
        AppLanguage.Italian => new[] { "Sintesi", "Argomenti", "Decisioni", "Attività", "Domande aperte" },
        AppLanguage.Portuguese => new[] { "Resumo", "Tópicos", "Decisões", "Tarefas", "Perguntas em aberto" },
        AppLanguage.Dutch => new[] { "Samenvatting", "Onderwerpen", "Besluiten", "Taken", "Open vragen" },
        AppLanguage.Polish => new[] { "Podsumowanie", "Tematy", "Decyzje", "Zadania", "Otwarte pytania" },
        AppLanguage.Swedish => new[] { "Sammanfattning", "Ämnen", "Beslut", "Uppgifter", "Öppna frågor" },
        AppLanguage.Danish => new[] { "Resumé", "Emner", "Beslutninger", "Opgaver", "Åbne spørgsmål" },
        _ => null,
    };

    /// <summary>Sprachname auf Deutsch („Deutsch“, „Englisch“), passend zum deutschen Standard-Prompt.</summary>
    public static string LanguageName(AppLanguage language) => language.DisplayName();
}
