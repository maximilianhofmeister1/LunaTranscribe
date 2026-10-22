using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LunaCore;
using ModelContextProtocol.Protocol;

namespace LunaMCP.Server;

/// <summary>Lesende Tools. Annotationen: nur lesend, geschlossene Welt (nur die lokale Bibliothek), wiederholbar.</summary>
internal static class ReadTools
{
    private static ToolAnnotations Annotations() =>
        new() { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false };

    public static readonly IReadOnlyList<ToolHandler> All = new[] { ListMeetings(), SearchMeetings(), GetMeeting(), ProcessingStatus() };

    // MARK: list_meetings

    private sealed record ListOutput(
        [property: JsonPropertyName("meetings")] List<MeetingInfo> Meetings,
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private static ToolHandler ListMeetings() => new(
        new Tool
        {
            Name = "list_meetings",
            Title = "Meetings auflisten",
            Description = "Listet Meetings aus LunaTranscribe, neueste zuerst: Datum, Titel, Dauer, Sprecher, Wortzahl, ob es eine "
                          + "Zusammenfassung gibt, und die Revision (für Änderungen). Mit from/to nach Zeitraum und mit speaker nach "
                          + "Person filtern. Für Themen oder Inhalte stattdessen search_meetings nutzen.",
            InputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["from"] = Schema.From(),
                ["to"] = Schema.To(),
                ["speaker"] = Schema.Speaker(),
                ["has_summary"] = Schema.Boolean("Nur Meetings mit (true) oder ohne (false) Zusammenfassung."),
                ["limit"] = Schema.Integer("Anzahl (Standard 20).", 1, 100),
                ["cursor"] = Schema.String("Fortsetzung aus next_cursor einer vorherigen Antwort."),
            })),
            OutputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["meetings"] = new JsonObject { ["type"] = "array", ["items"] = MeetingInfo.Schema() },
                ["total"] = Schema.Type("integer"),
                ["next_cursor"] = Schema.Type("string"),
            }, "meetings", "total")),
            Annotations = Annotations(),
        },
        Writes: false,
        Run: (args, context) =>
        {
            var filter = new SearchService.Filter(args.DayStart("from"), args.DayEnd("to"), args.String("speaker") is { } s ? TextFolding.Fold(s) : null);
            var hasSummary = args.Bool("has_summary");
            var limit = args.Int("limit", 20, 1, 100);
            var offset = args.Int("cursor", 0, 0, int.MaxValue);

            var matching = context.Snapshot.Meetings.Where(meeting =>
            {
                if (!SearchService.Matches(meeting, filter)) return false;
                if (hasSummary is { } wanted && (meeting.Transcript.Summary is not null) != wanted) return false;
                return true;
            }).ToList();
            var page = matching.Skip(offset).Take(limit).ToList();
            var next = offset + page.Count < matching.Count ? (offset + page.Count).ToString() : null;

            var lines = new List<string>();
            if (matching.Count == 0)
            {
                lines.Add(context.Snapshot.Meetings.Count == 0
                    ? "Die Bibliothek enthält noch keine (freigegebenen) Meetings."
                    : "Keine Meetings passen zu den Filtern.");
            }
            else
            {
                lines.Add($"{matching.Count} Meeting(s), neueste zuerst – {offset + 1} bis {offset + page.Count}:");
                lines.AddRange(page.Select(m => "- " + Rendering.ListLine(m) + " · Rev " + m.Revision));
                if (next is not null) lines.Add($"Weitere: list_meetings(cursor: \"{next}\") mit denselben Filtern.");
            }
            return Task.FromResult(Results.Structured(string.Join("\n", lines),
                new ListOutput(page.Select(MeetingInfo.Of).ToList(), matching.Count, next)));
        });

    // MARK: search_meetings

    private sealed record SearchHitOutput(
        [property: JsonPropertyName("meeting")] MeetingInfo Meeting,
        [property: JsonPropertyName("start")] double? Start,
        [property: JsonPropertyName("end")] double? End,
        [property: JsonPropertyName("turn_from")] int? TurnFrom,
        [property: JsonPropertyName("turn_to")] int? TurnTo,
        [property: JsonPropertyName("speakers")] List<string> Speakers,
        [property: JsonPropertyName("excerpt")] string Excerpt,
        [property: JsonPropertyName("sources")] List<string> Sources);

    private sealed record SearchOutput(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("hits")] List<SearchHitOutput> Hits,
        [property: JsonPropertyName("note")] string? Note);

    private static ToolHandler SearchMeetings() => new(
        new Tool
        {
            Name = "search_meetings",
            Title = "Meetings durchsuchen",
            Description = "Durchsucht alle Meetings nach Themen, Aussagen, Namen oder Begriffen – in Beiträgen, Zusammenfassungen, "
                          + "Titeln und Sprechernamen. Liefert Fundstellen mit Zeitmarke, Sprechern und Ausschnitt. Für Themen und "
                          + "Bedeutungen in natürlicher Sprache fragen („Streit über die Preiserhöhung“); für Namen, Zahlen und "
                          + "Fachbegriffe die genauen Wörter nutzen, Phrasen in Anführungszeichen. Danach mit get_meeting gezielt "
                          + "weiterlesen (from_time der Fundstelle).",
            InputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["query"] = Schema.String("Wonach gesucht wird."),
                ["mode"] = Schema.String("auto = Stichwort + inhaltlich (Standard), keyword = nur exakte Wörter, semantic = nur nach Bedeutung.",
                                         SearchService.ModeNames),
                ["from"] = Schema.From(),
                ["to"] = Schema.To(),
                ["speaker"] = Schema.Speaker(),
                ["limit"] = Schema.Integer("Anzahl Fundstellen (Standard 8).", 1, 30),
            }, "query")),
            OutputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["query"] = Schema.Type("string"),
                ["mode"] = Schema.Type("string"),
                ["note"] = Schema.Type("string"),
                ["hits"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Schema.Object(new JsonObject
                    {
                        ["meeting"] = MeetingInfo.Schema(),
                        ["start"] = Schema.Type("number"),
                        ["end"] = Schema.Type("number"),
                        ["turn_from"] = Schema.Type("integer"),
                        ["turn_to"] = Schema.Type("integer"),
                        ["speakers"] = Schema.StringArray(),
                        ["excerpt"] = Schema.Type("string"),
                        ["sources"] = Schema.StringArray(),
                    }, "meeting", "speakers", "excerpt", "sources"),
                },
            }, "query", "mode", "hits")),
            Annotations = Annotations(),
        },
        Writes: false,
        Run: async (args, context) =>
        {
            var query = args.RequiredString("query");
            var mode = SearchService.ParseMode(args.String("mode") ?? "auto")
                       ?? throw new ToolError("„mode“ muss auto, keyword oder semantic sein.");
            var filter = new SearchService.Filter(args.DayStart("from"), args.DayEnd("to"), args.String("speaker") is { } s ? TextFolding.Fold(s) : null);
            var limit = args.Int("limit", 8, 1, 30);

            SearchService.Result result;
            try
            {
                result = await context.Search.SearchAsync(query, mode, filter, limit, context.Snapshot, context.Settings);
            }
            catch (Exception ex)
            {
                throw new ToolError("Die Suche ist gerade nicht möglich: " + ex.Message);
            }
            if (mode == SearchService.Mode.Semantic && result.Hits.Count == 0 && result.Semantic.Note is { } semanticNote)
                throw new ToolError(semanticNote);

            var lines = new List<string> { $"Suche „{query}“ – {result.Hits.Count} Fundstelle(n):" };
            var hits = new List<SearchHitOutput>();
            var linked = new List<StoredMeeting>();
            var number = 0;
            foreach (var hit in result.Hits)
            {
                if (context.Snapshot.Meeting(hit.MeetingID) is not { } meeting) continue;
                number++;
                var t = meeting.Transcript;
                var sources = hit.Sources.SortedLabels();
                var speakers = (hit.Chunk?.SpeakerIDs ?? (IReadOnlyList<string>)t.SpeakerIDs).Select(t.DisplayName).ToList();
                string excerpt;
                var location = "";
                if (hit.Chunk is { } chunk)
                {
                    excerpt = hit.Snippet ?? Rendering.Excerpt(chunk, t);
                    location = chunk.Kind == ChunkKind.Summary
                        ? " — Zusammenfassung"
                        : $" — [{MeetingTranscript.Clock(chunk.Start)}–{MeetingTranscript.Clock(chunk.End)}] {string.Join(", ", speakers)}";
                }
                else
                {
                    excerpt = "Titel oder Sprechername passt.";
                }
                lines.Add("");
                lines.Add($"{number}. {MeetingLibrary.ShortID(t.Id)} · „{t.Title}“ · {Rendering.Date(t.Date)}{location} · {string.Join(" + ", sources)}");
                lines.Add(string.Join("\n", excerpt.Split('\n').Select(l => "   > " + l)));
                if (hit.Chunk is { Kind: ChunkKind.Turns } turns)
                    lines.Add($"   Weiterlesen: get_meeting(id: \"{MeetingLibrary.ShortID(t.Id)}\", from_time: \"{MeetingTranscript.Clock(turns.Start)}\")");
                hits.Add(new SearchHitOutput(MeetingInfo.Of(meeting), hit.Chunk?.Start, hit.Chunk?.End,
                                             hit.Chunk?.Turns?.Lower + 1, hit.Chunk?.Turns?.Upper + 1, speakers, excerpt, sources));
                if (!linked.Any(m => m.Id == meeting.Id)) linked.Add(meeting);
            }
            if (result.Hits.Count == 0)
                lines.Add("Nichts gefunden. Andere Wörter, eine allgemeinere Formulierung oder mode: semantic probieren.");
            if (result.Semantic.Note is { } note)
            {
                lines.Add("");
                lines.Add("Hinweis: " + note);
            }

            context.Record(ActivityAction.Search, linked.Select(m => m.Id));
            return Results.Structured(string.Join("\n", lines),
                new SearchOutput(query, SearchService.ModeName(mode), hits, result.Semantic.Note), linked);
        });

    // MARK: get_meeting

    private static ToolHandler GetMeeting() => new(
        new Tool
        {
            Name = "get_meeting",
            Title = "Meeting lesen",
            Description = "Liest ein Meeting als Markdown: Metadaten, Revision, Sprecherzuordnung, Zusammenfassung und Transkript mit "
                          + "Zeitmarken [mm:ss]. Lange Meetings werden gekürzt – die Antwort nennt dann den offset für die Fortsetzung. "
                          + "Mit speaker, from_time/to_time oder include nur einen Teil lesen.",
            InputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["id"] = Schema.MeetingID(),
                ["include"] = Schema.Array(Schema.String("Teil", new[] { "summary", "transcript" }), "Welche Teile (Standard: beide)."),
                ["speaker"] = Schema.String("Nur Beiträge dieses Sprechers (Name oder „Sprecher 2“)."),
                ["from_time"] = Schema.String("Ab Zeitmarke, z. B. 12:04."),
                ["to_time"] = Schema.String("Bis Zeitmarke, z. B. 20:00."),
                ["offset"] = Schema.Integer("Erster Beitrag (1-basiert), für die Fortsetzung.", 1, 100_000),
                ["max_chars"] = Schema.Integer("Längenbegrenzung (Standard 40000).", 2_000, 150_000),
            }, "id")),
            Annotations = Annotations(),
            Meta = new JsonObject { ["anthropic/maxResultSizeChars"] = 120_000 },
        },
        Writes: false,
        Run: (args, context) =>
        {
            var meeting = context.Snapshot.Resolve(args.RequiredString("id"));
            var include = (args.StringArray("include") ?? new List<string> { "summary", "transcript" }).ToHashSet();
            if (include.Count == 0 || include.Any(i => i is not ("summary" or "transcript")))
                throw new ToolError("„include“ darf nur summary und/oder transcript enthalten.");
            var options = new Rendering.MeetingOptions
            {
                IncludeSummary = include.Contains("summary"),
                IncludeTranscript = include.Contains("transcript"),
                Speaker = args.String("speaker") is { } s ? TextFolding.Fold(s) : null,
                FromTime = args.Time("from_time"),
                ToTime = args.Time("to_time"),
                Offset = args.Int("offset", 1, 1, 100_000),
                MaxChars = args.Int("max_chars", 40_000, 2_000, 150_000),
            };
            context.Record(ActivityAction.Read, new[] { meeting.Id });
            return Task.FromResult(Results.Text(Rendering.Meeting(meeting, options)));
        });

    // MARK: get_processing_status

    private sealed record StatusJob(
        [property: JsonPropertyName("recorded_at")] string RecordedAt,
        [property: JsonPropertyName("duration_seconds")] int DurationSeconds,
        [property: JsonPropertyName("failure")] string? Failure);

    private sealed record StatusOutput(
        [property: JsonPropertyName("pending")] List<StatusJob> Pending,
        [property: JsonPropertyName("latest_meeting")] MeetingInfo? LatestMeeting,
        [property: JsonPropertyName("index")] string Index);

    private static ToolHandler ProcessingStatus() => new(
        new Tool
        {
            Name = "get_processing_status",
            Title = "Verarbeitungsstand",
            Description = "Zeigt, ob beendete Meetings noch verarbeitet werden (Sprechererkennung, Transkription) oder fehlgeschlagen "
                          + "sind, welches Meeting zuletzt fertig wurde, und den Stand des Suchindex. Nützlich bei „Ist mein Meeting von "
                          + "eben schon fertig?“.",
            InputSchema = Schema.Element(Schema.Object(new JsonObject())),
            OutputSchema = Schema.Element(Schema.Object(new JsonObject
            {
                ["pending"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Schema.Object(new JsonObject
                    {
                        ["recorded_at"] = Schema.Type("string"),
                        ["duration_seconds"] = Schema.Type("integer"),
                        ["failure"] = Schema.Type("string"),
                    }, "recorded_at", "duration_seconds"),
                },
                ["latest_meeting"] = MeetingInfo.Schema(),
                ["index"] = Schema.Type("string"),
            }, "pending", "index")),
            Annotations = Annotations(),
        },
        Writes: false,
        Run: (_, context) =>
        {
            var text = StatusText(context);
            var jobs = MeetingJob.ReadAll(context.Environment.QueueFolder);
            var output = new StatusOutput(
                jobs.Select(j => new StatusJob(JsonCoding.FormatDate(j.RecordedAt), (int)Math.Round(j.DurationSeconds, MidpointRounding.AwayFromZero), j.Failure)).ToList(),
                context.Snapshot.Meetings.FirstOrDefault() is { } latest ? MeetingInfo.Of(latest) : null,
                context.Search.Stats(context.Snapshot, context.Settings));
            return Task.FromResult(Results.Structured(text, output));
        });

    /// <summary>Gemeinsam mit der Ressource <c>luna://status</c>.</summary>
    public static string StatusText(ToolContext context)
    {
        var jobs = MeetingJob.ReadAll(context.Environment.QueueFolder);
        var lines = new List<string>();
        if (jobs.Count == 0)
        {
            lines.Add("Keine Meetings in der Verarbeitung.");
        }
        else
        {
            lines.Add($"{jobs.Count} Meeting(s) in der Warteschlange (älteste zuerst):");
            foreach (var job in jobs)
            {
                var state = job.Failure is { } failure
                    ? $"fehlgeschlagen: {failure} – in LunaTranscribe erneut versuchen oder verwerfen"
                    : "wartet bzw. wird verarbeitet";
                lines.Add($"- Aufnahme vom {Rendering.Date(job.RecordedAt)}, {MeetingTranscript.Clock(job.DurationSeconds)} – {state}");
            }
        }
        if (context.Snapshot.Meetings.FirstOrDefault() is { } latest)
            lines.Add("Zuletzt fertig: " + Rendering.ListLine(latest));
        if (!context.Snapshot.LibraryReachable)
            lines.Add($"Achtung: Der Meeting-Ordner {context.Snapshot.LibraryFolder} ist nicht erreichbar (z. B. externes Laufwerk).");
        lines.Add(context.Search.Stats(context.Snapshot, context.Settings));
        return string.Join("\n", lines);
    }
}
