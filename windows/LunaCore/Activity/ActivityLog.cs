using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>Was Claude über luna-mcp getan hat. Gespeichert camelCase wie in Swift („renameMeeting“).</summary>
[JsonConverter(typeof(ActivityActionConverter))]
public enum ActivityAction
{
    Read,
    Search,
    RenameMeeting,
    RenameSpeaker,
    Delete,
    DeleteDeclined,
}

internal sealed class ActivityActionConverter : JsonStringEnumConverter<ActivityAction>
{
    public ActivityActionConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

/// <summary>
/// Ein Eintrag im Aktivitätsprotokoll von luna-mcp. Enthält nie Transkript-Inhalte – nur IDs und bei
/// Umbenennungen den alten und neuen Namen, damit die App sie rückgängig machen kann.
/// </summary>
public sealed record ActivityEntry
{
    [JsonPropertyName("date"), JsonRequired] public DateTime Date { get; init; }
    /// <summary>Name des Claude-Clients („claude-ai“, „claude-code“ …), wie er sich beim Verbinden meldet.</summary>
    [JsonPropertyName("client"), JsonRequired] public string Client { get; init; } = "";
    [JsonPropertyName("action"), JsonRequired] public ActivityAction Action { get; init; }
    [JsonPropertyName("meetingIDs"), JsonRequired] public IReadOnlyList<Guid> MeetingIDs { get; init; } = Array.Empty<Guid>();
    /// <summary>Sprecher-ID bei <see cref="ActivityAction.RenameSpeaker"/>.</summary>
    [JsonPropertyName("speakerID")] public string? SpeakerID { get; init; }
    [JsonPropertyName("oldValue")] public string? OldValue { get; init; }
    [JsonPropertyName("newValue")] public string? NewValue { get; init; }

    public ActivityEntry(string client, ActivityAction action, IEnumerable<Guid> meetingIDs, DateTime? date = null,
                         string? speakerID = null, string? oldValue = null, string? newValue = null)
    {
        Date = date ?? DateTime.UtcNow;
        Client = client;
        Action = action;
        MeetingIDs = meetingIDs.ToList();
        SpeakerID = speakerID;
        OldValue = oldValue;
        NewValue = newValue;
    }

    [JsonConstructor]
    private ActivityEntry() { }

    /// <summary>Hat dieser Eintrag die Bibliothek verändert? Die App lädt dann neu.</summary>
    [JsonIgnore]
    public bool IsChange => Action is ActivityAction.RenameMeeting or ActivityAction.RenameSpeaker or ActivityAction.Delete;

    public bool Equals(ActivityEntry? other) =>
        other is not null && Date == other.Date && Client == other.Client && Action == other.Action
        && MeetingIDs.SequenceEqual(other.MeetingIDs) && SpeakerID == other.SpeakerID
        && OldValue == other.OldValue && NewValue == other.NewValue;

    public override int GetHashCode() => HashCode.Combine(Date, Client, Action, MeetingIDs.Count);
}

/// <summary>
/// JSON-Lines-Datei (<see cref="AppPaths.McpActivityLog"/>). luna-mcp hängt an, die App liest (Anzeige, Rückgängig)
/// und beobachtet die Datei, um Änderungen von außen sofort zu übernehmen. Zeilen wie in der macOS-Version
/// (einzeilig, Schlüssel sortiert).
/// </summary>
public sealed class ActivityLog
{
    public string Path { get; }
    /// <summary>Ältere Einträge fallen beim Aufräumen weg.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
    /// <summary>Ab dieser Größe (Bytes) wird beim nächsten Anhängen aufgeräumt.</summary>
    public long CompactionThreshold { get; set; } = 512 * 1024;

    public ActivityLog(string? path = null)
    {
        Path = path ?? AppPaths.McpActivityLog;
    }

    public void Append(ActivityEntry entry)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var line = Encoding.UTF8.GetBytes(JsonCoding.Encode(entry, pretty: false) + "\n");
        if (!File.Exists(Path))
        {
            AtomicFile.WriteAllBytes(Path, line);
            return;
        }
        long size;
        using (var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            size = stream.Length;
            stream.Write(line);
        }
        if (size > CompactionThreshold) Compact();
    }

    /// <summary>Einträge, neueste zuerst. Beschädigte Zeilen werden übersprungen.</summary>
    public List<ActivityEntry> Entries(int? limit = null)
    {
        byte[] data;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new();
        }

        var all = new List<ActivityEntry>();
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != 0x0A) continue;
            if (i > start)
            {
                try { all.Add(JsonCoding.Decode<ActivityEntry>(new ReadOnlySpan<byte>(data, start, i - start))); }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { /* beschädigte Zeile */ }
            }
            start = i + 1;
        }
        all.Reverse();
        return limit is { } n ? all.Take(n).ToList() : all;
    }

    /// <summary>Wirft Einträge älter als <see cref="Retention"/> weg.</summary>
    public void Compact(DateTime? now = null)
    {
        var reference = JsonCoding.ToUtc(now ?? DateTime.UtcNow);
        var keep = Entries().AsEnumerable().Reverse().Where(e => reference - JsonCoding.ToUtc(e.Date) <= Retention);
        var sb = new StringBuilder();
        foreach (var entry in keep) sb.Append(JsonCoding.Encode(entry, pretty: false)).Append('\n');
        AtomicFile.WriteAllBytes(Path, Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
