using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>
/// Ein beendetes Meeting, das auf seine Verarbeitung wartet. Liegt als <c>job.json</c> neben der Aufnahme
/// in <c>AppPaths.MeetingQueue\&lt;id&gt;\</c> und übersteht damit einen Neustart der App. luna-mcp liest die
/// Aufträge nur, um den Verarbeitungsstand zu melden.
/// </summary>
public sealed record MeetingJob
{
    public const string JobFileName = "job.json";
    public const string AudioFileName = "audio.wav";

    [JsonPropertyName("id"), JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("recordedAt"), JsonRequired] public DateTime RecordedAt { get; init; }
    [JsonPropertyName("durationSeconds"), JsonRequired] public double DurationSeconds { get; init; }
    /// <summary>Beim Beenden festgelegt, damit spätere Einstellungsänderungen den Auftrag nicht verändern.</summary>
    [JsonPropertyName("language"), JsonRequired] public AppLanguage Language { get; init; }
    [JsonPropertyName("expectedSpeakers")] public int? ExpectedSpeakers { get; init; }
    /// <summary>Fehler des letzten Versuchs; solche Aufträge warten auf „Erneut versuchen“ oder „Verwerfen“.</summary>
    [JsonPropertyName("failure")] public string? Failure { get; set; }

    public MeetingJob(DateTime recordedAt, double durationSeconds, AppLanguage language, int? expectedSpeakers,
                      string? failure = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        RecordedAt = recordedAt;
        DurationSeconds = durationSeconds;
        Language = language;
        ExpectedSpeakers = expectedSpeakers;
        Failure = failure;
    }

    [JsonConstructor]
    private MeetingJob() { }

    [JsonIgnore] public string Title => MeetingTranscript.DefaultTitle(RecordedAt);

    /// <summary>Liest alle Aufträge aus dem Warteschlangen-Ordner, älteste zuerst. Unlesbare werden übersprungen.</summary>
    public static List<MeetingJob> ReadAll(string queueFolder)
    {
        string[] folders;
        try { folders = Directory.GetDirectories(queueFolder); }
        catch (Exception) { return new(); }

        var jobs = new List<MeetingJob>();
        foreach (var folder in folders)
        {
            try
            {
                var jobFile = Path.Combine(folder, JobFileName);
                if (!File.Exists(jobFile) || !File.Exists(Path.Combine(folder, AudioFileName))) continue;
                jobs.Add(JsonCoding.Decode<MeetingJob>(File.ReadAllBytes(jobFile)));
            }
            catch (Exception) { /* unlesbar – überspringen */ }
        }
        return jobs.OrderBy(j => j.RecordedAt).ToList();
    }
}
