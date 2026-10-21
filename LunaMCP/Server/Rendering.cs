using System;
using System.Collections.Generic;
using System.Linq;
using LunaCore;

namespace LunaMCP.Server;

/// <summary>
/// Textdarstellung für Claude: kompaktes Markdown statt <c>meeting.json</c> (keine UUIDs pro Beitrag, keine
/// Einrückung), Zeitmarken im Format der App, Sprechernamen aufgelöst.
/// </summary>
internal static class Rendering
{
    public static string Date(DateTime date) => MeetingTranscript.Formatters.HeaderDate(date);

    public static List<string> Speakers(MeetingTranscript transcript) => transcript.SpeakerIDs.Select(transcript.DisplayName).ToList();

    /// <summary>Meeting-ID klein, wie in URIs und Ordnernamen.</summary>
    public static string IdString(Guid id) => id.ToString("D").ToLowerInvariant();

    public static string Uri(Guid id) => "luna://meetings/" + IdString(id);

    /// <summary>„a1b2c3 · 25.09.2026, 14:03 · „Planung Q4“ · 47:12 · Anna, Sprecher 2 · 6210 Wörter · Zusammenfassung ✓“</summary>
    public static string ListLine(StoredMeeting meeting)
    {
        var t = meeting.Transcript;
        var parts = new List<string>
        {
            MeetingLibrary.ShortID(t.Id),
            Date(t.Date),
            $"„{t.Title}“",
            MeetingTranscript.Clock(t.DurationSeconds),
        };
        var names = Speakers(t);
        if (names.Count > 0) parts.Add(string.Join(", ", names));
        parts.Add($"{t.WordCount} Wörter");
        parts.Add(t.Summary is null ? "ohne Zusammenfassung" : "Zusammenfassung ✓");
        return string.Join(" · ", parts);
    }

    /// <summary>Kopf für <c>get_meeting</c> und Ressourcen: Titel, Metadaten, Revision, Sprecherzuordnung.</summary>
    public static List<string> Header(StoredMeeting meeting)
    {
        var t = meeting.Transcript;
        var lines = new List<string>
        {
            $"# {t.Title}",
            $"_{t.MetadataLine} · ID {MeetingLibrary.ShortID(t.Id)} · Revision {meeting.Revision}_",
        };
        var mapping = t.SpeakerIDs.Select(id =>
        {
            var name = t.DisplayName(id);
            return name == id ? id : $"{id} = {name}";
        }).ToList();
        if (mapping.Count > 0) lines.Add("Sprecher: " + string.Join(" · ", mapping));
        return lines;
    }

    public static string TurnLine(MeetingTranscript.Turn turn, MeetingTranscript transcript) =>
        $"[{MeetingTranscript.Clock(turn.Start)}] {transcript.DisplayName(turn.SpeakerID)}: {turn.Text}";

    public sealed class MeetingOptions
    {
        public bool IncludeSummary { get; set; } = true;
        public bool IncludeTranscript { get; set; } = true;
        /// <summary>Gefalteter Sprechername oder -ID.</summary>
        public string? Speaker { get; set; }
        public double? FromTime { get; set; }
        public double? ToTime { get; set; }
        /// <summary>Erster Beitrag, 1-basiert (wie in der Ausgabe gezählt).</summary>
        public int Offset { get; set; } = 1;
        public int MaxChars { get; set; } = 40_000;
    }

    /// <summary>Ganzes Meeting (oder Ausschnitt) als Markdown; kürzt bei <c>MaxChars</c> mit Fortsetzungshinweis.</summary>
    public static string Meeting(StoredMeeting meeting, MeetingOptions? options = null)
    {
        options ??= new MeetingOptions();
        var t = meeting.Transcript;
        var lines = Header(meeting);

        if (options.IncludeSummary)
        {
            lines.Add("");
            lines.Add("## Zusammenfassung");
            lines.Add("");
            lines.Add(t.Summary is { } summary
                ? MeetingTranscript.DemoteHeadings(summary.Markdown)
                : "_Noch keine Zusammenfassung. Der Nutzer kann in LunaTranscribe auf „Zusammenfassen“ klicken, oder du fasst mit dem Prompt meeting_summary zusammen._");
        }
        if (!options.IncludeTranscript) return string.Join("\n", lines);

        var speakerIDs = options.Speaker is { } speaker ? SearchService.SpeakerIDs(speaker, t) : null;
        var selected = Enumerable.Range(0, t.Turns.Count).Where(index =>
        {
            var turn = t.Turns[index];
            if (index + 1 < options.Offset) return false;
            if (options.FromTime is { } from && turn.End < from) return false;
            if (options.ToTime is { } to && turn.Start > to) return false;
            if (speakerIDs is not null && !speakerIDs.Contains(turn.SpeakerID)) return false;
            return true;
        }).ToList();

        var budget = options.MaxChars - lines.Sum(l => l.Length + 1);
        var body = new List<string>();
        int? cutAt = null;
        foreach (var index in selected)
        {
            var line = TurnLine(t.Turns[index], t);
            // Mindestens ein Beitrag, sonst käme man mit der Fortsetzung nie weiter.
            if (body.Count > 0 && budget - line.Length - 1 < 0)
            {
                cutAt = index;
                break;
            }
            body.Add(line);
            budget -= line.Length + 1;
        }

        lines.Add("");
        if (selected.Count == 0)
        {
            lines.Add("## Transkript");
            lines.Add("");
            lines.Add(t.Turns.Count == 0 ? "_Keine Beiträge erkannt._" : "_Keine Beiträge passen zu den Filtern._");
            return string.Join("\n", lines);
        }
        var first = selected[0] + 1;
        var last = (cutAt is { } cut ? selected[selected.IndexOf(cut) - 1] : selected[^1]) + 1;
        var filtered = options.Speaker is not null || options.FromTime is not null || options.ToTime is not null;
        lines.Add($"## Transkript (Beiträge {first}–{last} von {t.Turns.Count}{(filtered ? ", gefiltert" : "")})");
        lines.Add("");
        lines.AddRange(body);
        if (cutAt is { } next)
        {
            lines.Add("");
            lines.Add($"_Gekürzt bei {options.MaxChars} Zeichen. Fortsetzung: get_meeting(id: \"{MeetingLibrary.ShortID(t.Id)}\", offset: {next + 1}) – mit denselben Filtern._");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Beiträge eines Abschnitts mit Namen – für inhaltliche Treffer, die keinen Stichwort-Ausschnitt haben.</summary>
    public static string Excerpt(ChunkRecord record, MeetingTranscript transcript, int maxChars = 420)
    {
        if (record.Kind != ChunkKind.Turns || record.Turns is not { } range || range.Upper >= transcript.Turns.Count)
            return record.Text.Length > maxChars ? record.Text[..maxChars] : record.Text;
        var text = string.Join("\n", range.Indices.Select(i => TurnLine(transcript.Turns[i], transcript)));
        return text.Length > maxChars ? text[..maxChars] + " …" : text;
    }
}
