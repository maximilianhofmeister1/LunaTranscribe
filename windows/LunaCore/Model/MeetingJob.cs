using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>Wo ein Auftrag vor der Verarbeitung steht (Feld <c>phase</c> in <c>job.json</c>).</summary>
[JsonConverter(typeof(MeetingJobPhaseConverter))]
public enum MeetingJobPhase
{
    /// <summary>
    /// Die Aufnahme läuft noch, die WAV wächst. Läuft sie wirklich, hält der aufnehmende Prozess die
    /// <see cref="RecordingLock"/> im Auftragsordner.
    /// </summary>
    Recording,
    /// <summary>
    /// Die Aufnahme wurde nicht regulär beendet (Absturz, Herunterfahren) und beim nächsten Start gerettet.
    /// Wird erst verarbeitet, wenn der Nutzer „Transkription starten“ wählt.
    /// </summary>
    Interrupted,
}

/// <summary>
/// Ein Meeting in der Warteschlange. Liegt als <c>job.json</c> neben der Aufnahme in
/// <c>AppPaths.MeetingQueue\&lt;id&gt;\</c> und übersteht damit einen Neustart der App – und zwar von der ersten
/// Aufnahmesekunde an (<see cref="Phase"/> = Recording), damit auch ein Absturz oder Herunterfahren mitten im Meeting
/// nichts verliert. luna-mcp liest die Aufträge nur, um den Verarbeitungsstand zu melden.
/// </summary>
public sealed record MeetingJob
{
    public const string JobFileName = "job.json";
    public const string AudioFileName = "audio.wav";
    /// <summary>Sperrdatei einer laufenden Aufnahme (siehe <see cref="RecordingLock"/>).</summary>
    public const string LockFileName = "recording.lock";

    [JsonPropertyName("id"), JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("recordedAt"), JsonRequired] public DateTime RecordedAt { get; init; }
    [JsonPropertyName("durationSeconds"), JsonRequired] public double DurationSeconds { get; init; }
    /// <summary>
    /// Beim Beenden festgelegt, damit spätere Einstellungsänderungen den Auftrag nicht verändern
    /// (gerettete Aufnahmen behalten die Werte vom Start).
    /// </summary>
    [JsonPropertyName("language"), JsonRequired] public AppLanguage Language { get; init; }
    [JsonPropertyName("expectedSpeakers")] public int? ExpectedSpeakers { get; init; }
    /// <summary>Fehler des letzten Versuchs; solche Aufträge warten auf „Erneut versuchen“ oder „Verwerfen“.</summary>
    [JsonPropertyName("failure")] public string? Failure { get; set; }
    /// <summary>
    /// null (Feld fehlt) = beendet, wartet auf Verarbeitung – so sehen auch alle <c>job.json</c> aus, die vor
    /// Einführung des Felds geschrieben wurden.
    /// </summary>
    [JsonPropertyName("phase")] public MeetingJobPhase? Phase { get; init; }

    public MeetingJob(DateTime recordedAt, double durationSeconds, AppLanguage language, int? expectedSpeakers,
                      string? failure = null, Guid? id = null, MeetingJobPhase? phase = null)
    {
        Id = id ?? Guid.NewGuid();
        RecordedAt = recordedAt;
        DurationSeconds = durationSeconds;
        Language = language;
        ExpectedSpeakers = expectedSpeakers;
        Failure = failure;
        Phase = phase;
    }

    [JsonConstructor]
    private MeetingJob() { }

    [JsonIgnore] public string Title => MeetingTranscript.DefaultTitle(RecordedAt);

    /// <summary>Ordner eines Auftrags in der Warteschlange (UUID groß, wie die macOS-Version).</summary>
    public static string Folder(Guid id, string? queueFolder = null) =>
        Path.Combine(queueFolder ?? AppPaths.MeetingQueue, id.ToString("D").ToUpperInvariant());

    /// <summary>Liest <c>job.json</c> aus einem Auftragsordner.</summary>
    public static MeetingJob Read(string folder) => JsonCoding.Decode<MeetingJob>(File.ReadAllBytes(Path.Combine(folder, JobFileName)));

    /// <summary>Schreibt <c>job.json</c> atomar in den Auftragsordner.</summary>
    public void Write(string? queueFolder = null) =>
        AtomicFile.WriteAllBytes(Path.Combine(Folder(Id, queueFolder), JobFileName), JsonCoding.EncodeToUtf8(this));

    /// <summary>Läuft die Aufnahme dieses Auftrags gerade wirklich (Sperre gehalten, in irgendeinem Prozess)?</summary>
    public bool IsRecordingActive(string? queueFolder = null) =>
        Phase == MeetingJobPhase.Recording && RecordingLock.IsHeld(Path.Combine(Folder(Id, queueFolder), LockFileName));

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
                if (!File.Exists(Path.Combine(folder, JobFileName)) || !File.Exists(Path.Combine(folder, AudioFileName))) continue;
                jobs.Add(Read(folder));
            }
            catch (Exception) { /* unlesbar – überspringen */ }
        }
        return jobs.OrderBy(j => j.RecordedAt).ToList();
    }
}

/// <summary><see cref="MeetingJobPhase"/> als „recording“ / „interrupted“ (wie die rawValues der macOS-Version).</summary>
internal sealed class MeetingJobPhaseConverter : JsonConverter<MeetingJobPhase>
{
    public override MeetingJobPhase Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "recording" => MeetingJobPhase.Recording,
            "interrupted" => MeetingJobPhase.Interrupted,
            var other => throw new System.Text.Json.JsonException($"Unbekannte Phase: {other}"),
        };

    public override void Write(System.Text.Json.Utf8JsonWriter writer, MeetingJobPhase value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value == MeetingJobPhase.Recording ? "recording" : "interrupted");
}
