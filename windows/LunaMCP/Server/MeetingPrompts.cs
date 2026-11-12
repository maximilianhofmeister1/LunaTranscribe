using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using LunaCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LunaMCP.Server;

/// <summary>
/// Prompts: wiederverwendbare Arbeitsaufträge (Claude Code: Slash-Befehle, Claude Desktop: „+“-Menü).
/// Wo es um ein Meeting geht, wird es als Ressource eingebettet; übergreifende Prompts weisen Claude an,
/// die Tools zu nutzen.
/// </summary>
internal static class MeetingPrompts
{
    private static PromptArgument IdArgument() =>
        new() { Name = "id", Title = "Meeting", Description = "Meeting-ID (mindestens 6 Zeichen)", Required = true };

    public static List<Prompt> All() => new()
    {
        new Prompt
        {
            Name = "meeting_summary", Title = "Meeting zusammenfassen",
            Description = "Fasst ein Meeting im Format von LunaTranscribe zusammen (Kurzfassung, Themen, Entscheidungen, Aufgaben, offene Fragen).",
            Arguments = new List<PromptArgument> { IdArgument() },
        },
        new Prompt
        {
            Name = "weekly_review", Title = "Wochenrückblick",
            Description = "Entscheidungen, offene Aufgaben je Person und offene Fragen aller Meetings einer Woche.",
            Arguments = new List<PromptArgument>
            {
                new() { Name = "week", Title = "Woche", Description = "ISO-Woche wie 2026-W39, „letzte“ oder leer für diese Woche", Required = false },
            },
        },
        new Prompt
        {
            Name = "action_items", Title = "Aufgaben sammeln",
            Description = "Aufgaben mit Verantwortlichen und Fristen aus Meetings, jeweils mit Beleg.",
            Arguments = new List<PromptArgument>
            {
                new() { Name = "person", Title = "Person", Description = "Nur Aufgaben dieser Person", Required = false },
                new() { Name = "from", Title = "Von", Description = "JJJJ-MM-TT", Required = false },
                new() { Name = "to", Title = "Bis", Description = "JJJJ-MM-TT", Required = false },
            },
        },
        new Prompt
        {
            Name = "follow_up_email", Title = "Follow-up-Mail",
            Description = "Entwurf einer Follow-up-Mail zu einem Meeting.",
            Arguments = new List<PromptArgument>
            {
                IdArgument(),
                new() { Name = "recipient", Title = "Empfänger", Description = "An wen", Required = false },
            },
        },
        new Prompt
        {
            Name = "find_decision", Title = "Entscheidung finden",
            Description = "Wann und von wem wurde etwas entschieden? Mit Belegstellen.",
            Arguments = new List<PromptArgument>
            {
                new() { Name = "topic", Title = "Thema", Description = "Worum ging es", Required = true },
            },
        },
        new Prompt
        {
            Name = "name_speakers", Title = "Sprecher benennen",
            Description = "Schlägt Namen für „Sprecher n“ aus dem Gesprächsinhalt vor und benennt sie erst nach deiner Zustimmung.",
            Arguments = new List<PromptArgument> { IdArgument() },
        },
    };

    /// <exception cref="ToolError">Ungültige Argumente (der Aufrufer meldet sie als <c>invalidParams</c>).</exception>
    public static GetPromptResult Get(string name, IDictionary<string, JsonElement>? arguments, ToolContext context)
    {
        var args = new Arguments(arguments, context.Environment.TimeZone);
        switch (name)
        {
            case "meeting_summary":
            {
                var meeting = context.Snapshot.Resolve(args.RequiredString("id"));
                // Dieselbe Standardsprache wie der Knopf „Zusammenfassen“ in der App.
                var language = context.Settings.SummaryLanguage.Resolved(meeting.Transcript.Language);
                var instructions = SummaryPrompt.Instructions(context.Settings.SummaryPromptTemplate, language);
                return Embedding(meeting, context, $"Zusammenfassung von „{meeting.Transcript.Title}“", instructions);
            }
            case "weekly_review":
            {
                var (start, end) = Week(args.String("week"), context);
                var lastDay = end.AddDays(-1);
                return Plain("Wochenrückblick", string.Join("\n",
                    $"Erstelle einen Wochenrückblick über meine Meetings vom {Day(start)} bis {Day(lastDay)}.",
                    $"Hole die Meetings mit list_meetings(from: \"{Iso(start)}\", to: \"{Iso(lastDay)}\") und lies jedes mit "
                    + "get_meeting (die Zusammenfassung reicht, wo es eine gibt; sonst das Transkript).",
                    "",
                    "Gliederung:",
                    "## Überblick – ein Satz je Meeting",
                    "## Entscheidungen – mit Meeting und Zeitmarke als Beleg",
                    "## Offene Aufgaben je Person – „- [ ] Person: Aufgabe (Frist)“, mit Beleg",
                    "## Offene Fragen und Risiken",
                    "",
                    "Erfinde nichts; wo Sprecher nur „Sprecher n“ heißen, übernimm das so."));
            }
            case "action_items":
            {
                var person = args.String("person");
                var range = string.Join(", ", new[]
                {
                    args.String("from") is { } from ? $"from: \"{from}\"" : null,
                    args.String("to") is { } to ? $"to: \"{to}\"" : null,
                }.Where(x => x is not null));
                return Plain("Aufgaben", string.Join("\n",
                    $"Sammle die Aufgaben aus meinen Meetings{(person is null ? "" : " für " + person)}{(range.Length == 0 ? " der letzten zwei Wochen" : "")}.",
                    $"Nutze list_meetings({range}) und search_meetings (z. B. „Aufgabe“, „bis Freitag“, „kümmert sich“) und lies die "
                    + "Fundstellen mit get_meeting nach.",
                    "Format: „- [ ] Person: Aufgabe (Frist) – Meeting, [mm:ss]“. Nur, was wirklich vereinbart wurde; Unklares separat "
                    + "unter „Unklar“."));
            }
            case "follow_up_email":
            {
                var meeting = context.Snapshot.Resolve(args.RequiredString("id"));
                var recipient = args.String("recipient");
                return Embedding(meeting, context, "Follow-up-Mail",
                    $"Schreibe den Entwurf einer Follow-up-Mail{(recipient is null ? "" : " an " + recipient)} zu diesem Meeting: kurzer Dank, "
                    + "Zusammenfassung in zwei, drei Sätzen, Entscheidungen, nächste Schritte mit Verantwortlichen und Fristen. "
                    + "Sprache und Ton wie im Meeting (Du/Sie). Nichts erfinden.");
            }
            case "find_decision":
            {
                var topic = args.RequiredString("topic");
                return Plain("Entscheidung finden",
                    $"Finde heraus, wann und von wem zu „{topic}“ entschieden wurde. Suche mit search_meetings (erst inhaltlich, dann "
                    + "mit genauen Begriffen), lies die Fundstellen mit get_meeting nach und antworte mit Datum, Meeting, Person und "
                    + "wörtlichem Zitat samt Zeitmarke. Gab es mehrere Entscheidungen, zeige die Entwicklung. Findest du nichts "
                    + "Eindeutiges, sag das.");
            }
            case "name_speakers":
            {
                var meeting = context.Snapshot.Resolve(args.RequiredString("id"));
                return Embedding(meeting, context, "Sprecher benennen",
                    "Schlage für die Sprecher dieses Meetings Namen vor – nur, wo der Gesprächsinhalt sie belegt (Anrede, "
                    + "Vorstellung, „danke, Anna“). Nenne zu jedem Vorschlag die Belegstelle mit Zeitmarke und wie sicher du bist. "
                    + "Frag mich dann, welche du übernehmen sollst, und benenne erst nach meiner Zustimmung mit rename_speaker "
                    + $"(Revision: {meeting.Revision}). Keine Namen raten.");
            }
            default:
                throw new McpProtocolException($"Unbekannter Prompt „{name}“.", McpErrorCode.InvalidParams);
        }
    }

    // MARK: Hilfen

    private static GetPromptResult Plain(string description, string text) => new()
    {
        Description = description,
        Messages = new List<PromptMessage> { new() { Role = Role.User, Content = new TextContentBlock { Text = text } } },
    };

    private static GetPromptResult Embedding(StoredMeeting meeting, ToolContext context, string description, string instructions)
    {
        context.Record(ActivityAction.Read, new[] { meeting.Id });
        var markdown = Rendering.Meeting(meeting, new Rendering.MeetingOptions { MaxChars = 500_000 });
        return new GetPromptResult
        {
            Description = description,
            Messages = new List<PromptMessage>
            {
                new()
                {
                    Role = Role.User,
                    Content = new EmbeddedResourceBlock
                    {
                        Resource = new TextResourceContents { Uri = Rendering.Uri(meeting.Id), MimeType = "text/markdown", Text = markdown },
                    },
                },
                new() { Role = Role.User, Content = new TextContentBlock { Text = instructions } },
            },
        };
    }

    /// <summary>Montag 0:00 bis Montag 0:00 der gewünschten ISO-Woche, als Kalendertage in der Zeitzone des Nutzers.</summary>
    public static (DateTime Start, DateTime End) Week(string? text, ToolContext context)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(JsonCoding.ToUtc(context.Now), context.Environment.TimeZone).Date;
        var reference = today;
        if (!string.IsNullOrEmpty(text))
        {
            var lowered = text.ToLowerInvariant();
            if (lowered.StartsWith("letzt", StringComparison.Ordinal) || lowered == "last")
            {
                reference = today.AddDays(-7);
            }
            else if (lowered.StartsWith("dies", StringComparison.Ordinal) || lowered == "this")
            {
                reference = today;
            }
            else
            {
                var parts = lowered.Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || !parts[1].StartsWith('w')
                    || !int.TryParse(parts[1][1..], out var number) || number < 1 || number > ISOWeek.GetWeeksInYear(Math.Clamp(year, 1, 9998)))
                    throw new ToolError("„week“ muss wie 2026-W39 aussehen (oder „letzte“ / leer).");
                reference = ISOWeek.ToDateTime(year, number, DayOfWeek.Monday);
            }
        }
        var offset = ((int)reference.DayOfWeek + 6) % 7;   // Montag = 0
        var start = reference.AddDays(-offset);
        return (start, start.AddDays(7));
    }

    private static string Day(DateTime date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Iso(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
