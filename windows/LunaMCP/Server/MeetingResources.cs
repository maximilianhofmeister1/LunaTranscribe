using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LunaCore;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace LunaMCP.Server;

/// <summary>Ressourcen: vom Nutzer gewählter Kontext (Claude Code: <c>@</c>-Erwähnung, Claude Desktop: „+“-Menü).</summary>
internal static class MeetingResources
{
    public const string OverviewUri = "luna://meetings";
    public const string VocabularyUri = "luna://vocabulary";
    public const string StatusUri = "luna://status";
    public const int ListedMeetings = 50;

    public static List<Resource> Fixed() => new()
    {
        new Resource { Name = "meetings", Uri = OverviewUri, Title = "Meeting-Übersicht",
                       Description = $"Die letzten {ListedMeetings} Meetings mit Datum, Dauer und Sprechern.", MimeType = "text/markdown" },
        new Resource { Name = "vocabulary", Uri = VocabularyUri, Title = "Eigenes Wörterbuch",
                       Description = "Richtige Schreibweisen von Namen und Fachbegriffen.", MimeType = "text/markdown" },
        new Resource { Name = "status", Uri = StatusUri, Title = "Verarbeitungsstand",
                       Description = "Wartende Meetings und Stand des Suchindex.", MimeType = "text/markdown" },
    };

    public static List<Resource> List(ToolContext context)
    {
        var resources = Fixed();
        foreach (var meeting in context.Snapshot.Meetings.Take(ListedMeetings))
        {
            var t = meeting.Transcript;
            DateTimeOffset modified;
            try { modified = File.GetLastWriteTimeUtc(Path.Combine(meeting.Folder, MeetingLibrary.TranscriptFileName)); }
            catch (Exception) { modified = JsonCoding.ToUtc(t.Date); }
            resources.Add(new Resource
            {
                Name = MeetingLibrary.ShortID(t.Id),
                Uri = Rendering.Uri(t.Id),
                Title = $"{t.Title} · {Rendering.Date(t.Date)}",
                Description = $"{MeetingTranscript.Clock(t.DurationSeconds)} · {string.Join(", ", Rendering.Speakers(t))}",
                MimeType = "text/markdown",
                Annotations = new Annotations { LastModified = modified },
            });
        }
        return resources;
    }

    public static List<ResourceTemplate> Templates() => new()
    {
        new ResourceTemplate { UriTemplate = "luna://meetings/{id}", Name = "meeting", Title = "Meeting",
                               Description = "Ein Meeting als Markdown: Zusammenfassung und Transkript.", MimeType = "text/markdown" },
        new ResourceTemplate { UriTemplate = "luna://meetings/{id}/summary", Name = "meeting-summary", Title = "Zusammenfassung",
                               Description = "Nur die Zusammenfassung eines Meetings.", MimeType = "text/markdown" },
        new ResourceTemplate { UriTemplate = "luna://meetings/{id}/transcript.json", Name = "meeting-json", Title = "meeting.json",
                               Description = "Die Originaldatei – für Auswertungen per Code.", MimeType = "application/json" },
    };

    private static ReadResourceResult Text(string uri, string text, string mimeType = "text/markdown") => new()
    {
        Contents = new List<ResourceContents> { new TextResourceContents { Uri = uri, MimeType = mimeType, Text = text } },
    };

    public static ReadResourceResult Read(string uri, ToolContext context)
    {
        switch (uri)
        {
            case OverviewUri:
                var lines = context.Snapshot.Meetings.Take(ListedMeetings).Select(m => "- " + Rendering.ListLine(m)).ToList();
                return Text(uri, lines.Count == 0
                    ? "Noch keine (freigegebenen) Meetings."
                    : "# Meetings (neueste zuerst)\n\n" + string.Join("\n", lines));
            case VocabularyUri:
                return Text(uri, VocabularyText(context.Settings));
            case StatusUri:
                return Text(uri, ReadTools.StatusText(context));
        }

        const string prefix = OverviewUri + "/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) throw NotFound(uri);
        var parts = uri[prefix.Length..].Split('/', 2);
        if (parts[0].Length == 0) throw NotFound(uri);
        StoredMeeting meeting;
        try
        {
            meeting = context.Snapshot.Resolve(Uri.UnescapeDataString(parts[0]));
        }
        catch (ToolError error)
        {
            throw new McpProtocolException(error.Message, McpErrorCode.ResourceNotFound);
        }

        var part = parts.Length == 2 ? parts[1] : "";
        switch (part)
        {
            case "":
                context.Record(ActivityAction.Read, new[] { meeting.Id });
                return Text(uri, Rendering.Meeting(meeting, new Rendering.MeetingOptions { MaxChars = 500_000 }));
            case "summary":
                context.Record(ActivityAction.Read, new[] { meeting.Id });
                return Text(uri, Rendering.Meeting(meeting, new Rendering.MeetingOptions { IncludeTranscript = false }));
            case "transcript.json":
                context.Record(ActivityAction.Read, new[] { meeting.Id });
                var data = File.ReadAllBytes(Path.Combine(meeting.Folder, MeetingLibrary.TranscriptFileName));
                return Text(uri, Encoding.UTF8.GetString(data), "application/json");
            default:
                throw NotFound(uri);
        }
    }

    /// <summary>Vorschläge für <c>{id}</c>: passende Kurz-IDs nach ID-Anfang, Titel oder Datum („25.09“).</summary>
    public static List<string> Complete(string value, CatalogSnapshot snapshot)
    {
        var needle = TextFolding.Fold(value);
        return snapshot.Meetings
            .Where(m =>
            {
                if (needle.Length == 0) return true;
                var t = m.Transcript;
                return MeetingLibrary.ShortID(t.Id).StartsWith(needle, StringComparison.Ordinal)
                       || TextFolding.Fold(t.Title).Contains(needle, StringComparison.Ordinal)
                       || Rendering.Date(t.Date).Contains(needle, StringComparison.Ordinal);
            })
            .Take(20)
            .Select(m => MeetingLibrary.ShortID(m.Id))
            .ToList();
    }

    public static string VocabularyText(SharedSettings settings)
    {
        var entries = settings.VocabularyEntries.Where(e => e.Term.Trim().Length > 0).ToList();
        if (entries.Count == 0) return "Das Wörterbuch ist leer.";
        var lines = new List<string>
        {
            "# Eigenes Wörterbuch", "",
            "Richtige Schreibweisen – bitte in Antworten so verwenden. Transkripte können noch die Fehlschreibungen enthalten.", "",
        };
        if (!settings.VocabularyEnabled) lines.Add("_Das Wörterbuch ist in LunaTranscribe gerade ausgeschaltet._\n");
        foreach (var entry in entries)
        {
            var aliases = entry.Aliases.Where(a => a.Length > 0).ToList();
            lines.Add($"- **{entry.Term}**" + (aliases.Count == 0 ? "" : $" (falsch erkannt als: {string.Join(", ", aliases)})"));
        }
        return string.Join("\n", lines);
    }

    private static McpProtocolException NotFound(string uri) => new(
        $"Unbekannte Ressource {uri}. Verfügbar: {OverviewUri}, {OverviewUri}/{{id}}, …/summary, …/transcript.json, {VocabularyUri}, {StatusUri}.",
        McpErrorCode.ResourceNotFound);
}
