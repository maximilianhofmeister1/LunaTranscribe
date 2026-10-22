using System;
using System.IO;
using System.Text.Json;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

/// <summary>Das JSON muss mit der macOS-Version austauschbar sein (geteilter Meeting-Ordner).</summary>
public class JsonCodingTests
{
    [Fact(DisplayName = "Liest das bestehende v0.2-Format der macOS-Version, ohne neues Feld isConfidential")]
    public void DecodesMacFixture()
    {
        var t = JsonCoding.Decode<MeetingTranscript>(Support.FixtureText("meeting-v0.2.json"));
        Assert.Equal("Planung Q4", t.Title);
        Assert.Equal(Guid.Parse("A1B2C3D4-0000-4000-8000-000000000001"), t.Id);
        Assert.Equal(new DateTime(2026, 9, 15, 12, 3, 0, DateTimeKind.Utc), t.Date);
        Assert.Equal(DateTimeKind.Utc, t.Date.Kind);
        Assert.Equal(125.5, t.DurationSeconds);
        Assert.Equal(EngineKind.Parakeet, t.Engine);
        Assert.Equal(AppLanguage.German, t.Language);
        Assert.Equal(2, t.Turns.Count);
        Assert.Equal(6.2, t.Turns[0].End);
        Assert.Equal("Anna", t.DisplayName("Sprecher 1"));
        Assert.Equal("Sprecher 2", t.DisplayName("Sprecher 2"));
        Assert.Null(t.IsConfidential);
        Assert.False(t.Confidential);
        Assert.Null(t.Summary!.Model);
        Assert.Equal(new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc), t.Summary.CreatedAt);
    }

    [Fact(DisplayName = "Rundreise: gleiche Bytes wie der Swift-Encoder")]
    public void RoundTripIsByteIdentical()
    {
        var original = Support.FixtureText("meeting-v0.2.json").TrimEnd('\n');
        var decoded = JsonCoding.Decode<MeetingTranscript>(original);
        Assert.Equal(original, JsonCoding.Encode(decoded));
        Assert.Equal(decoded, JsonCoding.Decode<MeetingTranscript>(JsonCoding.EncodeToUtf8(decoded)));
    }

    [Fact(DisplayName = "Optionale Felder fehlen bei null, sind sonst vorhanden; Umlaute und Slashes unmaskiert")]
    public void OptionalFieldsAndEscaping()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Größe/Preis \"ok\"\n") }, title: "Über/Unter");
        var json = JsonCoding.Encode(meeting);
        Assert.DoesNotContain("isConfidential", json);
        Assert.DoesNotContain("importedFrom", json);
        Assert.DoesNotContain("summary", json);
        Assert.Contains("\"title\" : \"Über/Unter\"", json);
        Assert.Contains("\"text\" : \"Größe/Preis \\\"ok\\\"\\n\"", json);
        Assert.Contains("\"speakerNames\" : {}", json);
        Assert.Contains($"\"id\" : \"{meeting.Id.ToString().ToUpperInvariant()}\"", json);
        Assert.Contains("\"date\" : \"2026-09-21T", json);

        meeting.IsConfidential = true;
        meeting.ImportedFrom = "alt.txt";
        meeting.Summary = new MeetingSummary("x", Support.SampleDate, "sonnet");
        json = JsonCoding.Encode(meeting);
        Assert.Contains("\"isConfidential\" : true", json);
        Assert.Contains("\"importedFrom\" : \"alt.txt\"", json);
        Assert.Contains("\"model\" : \"sonnet\"", json);
        Assert.Equal(meeting, JsonCoding.Decode<MeetingTranscript>(json));
    }

    [Fact(DisplayName = "Daten: UTC ohne Sekundenbruchteile; Ortszeit wird umgerechnet")]
    public void Dates()
    {
        Assert.Equal("2026-09-25T14:03:00Z", JsonCoding.FormatDate(new DateTime(2026, 9, 25, 14, 3, 0, 999, DateTimeKind.Utc)));
        var local = new DateTime(2026, 9, 25, 14, 3, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), JsonCoding.ParseDate(JsonCoding.FormatDate(local)));
        Assert.Equal(new DateTime(2026, 9, 25, 12, 3, 0, DateTimeKind.Utc), JsonCoding.ParseDate("2026-09-25T14:03:00+02:00"));
        Assert.Equal(new DateTime(2026, 9, 25, 14, 3, 0, 500, DateTimeKind.Utc), JsonCoding.ParseDate("2026-09-25T14:03:00.5Z"));
    }

    [Fact(DisplayName = "Pflichtfelder und unbekannte Werte werden abgelehnt")]
    public void RejectsInvalid()
    {
        var json = Support.FixtureText("meeting-v0.2.json");
        Assert.ThrowsAny<JsonException>(() => JsonCoding.Decode<MeetingTranscript>(json.Replace("\"title\" : \"Planung Q4\",", "")));
        Assert.ThrowsAny<JsonException>(() => JsonCoding.Decode<MeetingTranscript>(json.Replace("\"parakeet\"", "\"whisper\"")));
        Assert.ThrowsAny<JsonException>(() => JsonCoding.Decode<MeetingTranscript>(json.Replace("\"de\"", "\"xx\"")));
    }

    [Fact(DisplayName = "Sprecher-Schlüssel werden numerisch sortiert wie von Foundation")]
    public void SortsKeysNumerically()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "a") });
        meeting.SpeakerNames["Sprecher 10"] = "Zehn";
        meeting.SpeakerNames["Sprecher 2"] = "Zwei";
        var json = JsonCoding.Encode(meeting);
        Assert.True(json.IndexOf("Sprecher 2", StringComparison.Ordinal) < json.IndexOf("Sprecher 10", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Auftrag (job.json): Rundreise und Warteschlange lesen")]
    public void MeetingJobs()
    {
        using var temp = new TempFolder();
        var newer = new MeetingJob(Support.SampleDate.AddHours(1), 60, AppLanguage.English, expectedSpeakers: 2);
        var older = new MeetingJob(Support.SampleDate, 30, AppLanguage.German, expectedSpeakers: null, failure: "Kaputt");
        foreach (var job in new[] { newer, older })
        {
            var folder = Path.Combine(temp.Path, job.Id.ToString().ToUpperInvariant());
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, MeetingJob.JobFileName), JsonCoding.EncodeToUtf8(job));
            File.WriteAllBytes(Path.Combine(folder, MeetingJob.AudioFileName), new byte[] { 0 });
        }
        Directory.CreateDirectory(Path.Combine(temp.Path, "ohne-audio"));
        File.WriteAllBytes(Path.Combine(temp.Path, "ohne-audio", MeetingJob.JobFileName), JsonCoding.EncodeToUtf8(newer));

        var json = JsonCoding.Encode(older);
        Assert.DoesNotContain("expectedSpeakers", json);
        Assert.Contains("\"language\" : \"de\"", json);
        Assert.Equal(new[] { older, newer }, MeetingJob.ReadAll(temp.Path));
        Assert.Empty(MeetingJob.ReadAll(Path.Combine(temp.Path, "gibt-es-nicht")));
        Assert.StartsWith("Meeting vom ", older.Title);
    }
}
