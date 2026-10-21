using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LunaCore;
using ModelContextProtocol.Protocol;

namespace LunaMCP.Server;

/// <summary>
/// Fachlicher Fehler eines Tools. Wird als Tool-Ergebnis mit <c>isError: true</c> gemeldet (nicht als
/// JSON-RPC-Fehler), damit Claude die Meldung liest und sich selbst korrigieren kann – so empfiehlt es
/// die MCP-Spezifikation auch für ungültige Eingaben. Die Meldung nennt immer den nächsten Schritt.
/// </summary>
public sealed class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

/// <summary>Alles, was ein Tool-Aufruf braucht – pro Anfrage frisch (Einstellungen, Katalog-Stand, Client-Name).</summary>
internal sealed record ToolContext(ServerEnvironment Environment, SharedSettings Settings, CatalogSnapshot Snapshot,
                                   SearchService Search, ConfirmationGuard Guardian, AuditTrail Audit, string Client)
{
    public DateTime Now => Environment.Now();

    public void Record(ActivityAction action, IEnumerable<Guid> ids, string? speakerID = null, string? oldValue = null, string? newValue = null) =>
        Audit.Record(new ActivityEntry(Client, action, ids, Now, speakerID, oldValue, newValue));
}

/// <summary>Ein Tool: Definition für <c>tools/list</c> plus Ausführung.</summary>
/// <param name="Writes">Nur sichtbar und aufrufbar, wenn der Nutzer Änderungen erlaubt hat.</param>
internal sealed record ToolHandler(Tool Tool, bool Writes, Func<Arguments, ToolContext, Task<CallToolResult>> Run);

/// <summary>
/// Schreibt das Aktivitätsprotokoll der Reihe nach (mehrere Tool-Aufrufe können gleichzeitig laufen).
/// Ein Fehler beim Protokollieren darf keinen Aufruf scheitern lassen.
/// </summary>
internal sealed class AuditTrail
{
    private readonly ActivityLog _log;
    private readonly object _gate = new();

    public AuditTrail(ActivityLog log) => _log = log;

    public void Record(ActivityEntry entry)
    {
        lock (_gate)
        {
            try { _log.Append(entry); }
            catch (Exception ex) { McpLog.Write("Protokoll nicht geschrieben: " + ex.Message); }
        }
    }
}

/// <summary>Kurzschreibweisen für JSON-Schemas.</summary>
internal static class Schema
{
    public static JsonObject Object(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        return schema;
    }

    public static JsonObject String(string description, IEnumerable<string>? oneOf = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (oneOf is not null) schema["enum"] = new JsonArray(oneOf.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());
        return schema;
    }

    public static JsonObject Integer(string description, long minimum, long maximum) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };

    public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Array(JsonNode items, string description, int? minItems = null, int? maxItems = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["items"] = items, ["description"] = description };
        if (minItems is { } min) schema["minItems"] = min;
        if (maxItems is { } max) schema["maxItems"] = max;
        return schema;
    }

    public static JsonObject Type(string type, string? description = null)
    {
        var schema = new JsonObject { ["type"] = type };
        if (description is not null) schema["description"] = description;
        return schema;
    }

    public static JsonObject StringArray() => new() { ["type"] = "array", ["items"] = Type("string") };

    public static JsonObject MeetingID() => String("Meeting-ID: die volle ID oder mindestens die ersten 6 Zeichen (wie in list_meetings).");
    public static JsonObject Revision() => String("Revision aus get_meeting oder list_meetings. Schützt davor, eine inzwischen geänderte Fassung zu überschreiben.");
    public static JsonObject From() => String("Frühestes Datum, JJJJ-MM-TT (inklusiv, Zeitzone des Nutzers).");
    public static JsonObject To() => String("Spätestes Datum, JJJJ-MM-TT (inklusiv).");
    public static JsonObject Speaker() => String("Nur Meetings/Beiträge dieses Sprechers (Name oder „Sprecher 2“).");

    public static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);
}

/// <summary>Strukturierte Ausgabe eines Meetings (für <c>structuredContent</c>).</summary>
internal sealed record MeetingInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("short_id")] public string ShortID { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("date")] public string Date { get; init; } = "";
    [JsonPropertyName("duration_seconds")] public int DurationSeconds { get; init; }
    [JsonPropertyName("speakers")] public IReadOnlyList<string> Speakers { get; init; } = System.Array.Empty<string>();
    [JsonPropertyName("word_count")] public int WordCount { get; init; }
    [JsonPropertyName("has_summary")] public bool HasSummary { get; init; }
    [JsonPropertyName("has_audio")] public bool HasAudio { get; init; }
    [JsonPropertyName("revision")] public string Revision { get; init; } = "";

    public static MeetingInfo Of(StoredMeeting meeting)
    {
        var t = meeting.Transcript;
        return new MeetingInfo
        {
            Id = Rendering.IdString(t.Id),
            ShortID = MeetingLibrary.ShortID(t.Id),
            Title = t.Title,
            Date = JsonCoding.FormatDate(t.Date),
            DurationSeconds = (int)Math.Round(t.DurationSeconds, MidpointRounding.AwayFromZero),
            Speakers = Rendering.Speakers(t),
            WordCount = t.WordCount,
            HasSummary = t.Summary is not null,
            HasAudio = meeting.HasAudio,
            Revision = meeting.Revision,
        };
    }

    public static JsonObject Schema() => LunaMCP.Server.Schema.Object(new JsonObject
    {
        ["id"] = LunaMCP.Server.Schema.Type("string"),
        ["short_id"] = LunaMCP.Server.Schema.Type("string"),
        ["title"] = LunaMCP.Server.Schema.Type("string"),
        ["date"] = LunaMCP.Server.Schema.Type("string", "ISO 8601"),
        ["duration_seconds"] = LunaMCP.Server.Schema.Type("integer"),
        ["speakers"] = LunaMCP.Server.Schema.StringArray(),
        ["word_count"] = LunaMCP.Server.Schema.Type("integer"),
        ["has_summary"] = LunaMCP.Server.Schema.Type("boolean"),
        ["has_audio"] = LunaMCP.Server.Schema.Type("boolean"),
        ["revision"] = LunaMCP.Server.Schema.Type("string"),
    }, "id", "short_id", "title", "date", "duration_seconds", "speakers", "word_count", "has_summary", "has_audio", "revision");
}

/// <summary>Bausteine für Tool-Ergebnisse.</summary>
internal static class Results
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CallToolResult Text(string text, IEnumerable<StoredMeeting>? links = null) => new()
    {
        Content = Blocks(text, links),
    };

    public static CallToolResult Structured<T>(string text, T output, IEnumerable<StoredMeeting>? links = null) => new()
    {
        Content = Blocks(text, links),
        StructuredContent = JsonSerializer.SerializeToElement(output, Options),
    };

    public static CallToolResult Failure(string message) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
        IsError = true,
    };

    private static List<ContentBlock> Blocks(string text, IEnumerable<StoredMeeting>? links)
    {
        var blocks = new List<ContentBlock> { new TextContentBlock { Text = text } };
        foreach (var meeting in links ?? Enumerable.Empty<StoredMeeting>())
        {
            blocks.Add(new ResourceLinkBlock
            {
                Uri = Rendering.Uri(meeting.Id),
                Name = MeetingLibrary.ShortID(meeting.Id),
                Title = meeting.Transcript.Title,
                Description = Rendering.Date(meeting.Transcript.Date),
                MimeType = "text/markdown",
            });
        }
        return blocks;
    }

    /// <summary>Der Text aller Textblöcke (für Selbsttest und Tests).</summary>
    public static string TextOf(IEnumerable<ContentBlock> content) =>
        string.Join("\n", content.OfType<TextContentBlock>().Select(b => b.Text));
}
