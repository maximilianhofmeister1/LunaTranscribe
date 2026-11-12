using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

/// <summary>Aufnahmen retten: WAV-Reparatur, Sperre, verwaiste Entwürfe, Altlasten (Port der macOS-Tests).</summary>
public class RecordingRecoveryTests
{
    private const int Rate = 16_000;

    /// <summary>Schreibt <paramref name="seconds"/> Sekunde(n) Sinus als PCM16 mit vollständigem Kopf (wie NAudio).</summary>
    private static void WriteWav(string path, double seconds)
    {
        var frames = (int)(seconds * Rate);
        var data = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(Math.Sin(i * 0.05) * 0.3 * short.MaxValue));
        using var stream = File.Create(path);
        stream.Write(WavHeader.Pcm16Header(Rate, 1, data.Length));
        stream.Write(data);
    }

    /// <summary>Simuliert einen Absturz: Größen im Kopf auf <paramref name="declared"/> setzen, optional ein halbes Frame anhängen.</summary>
    private static void BreakHeader(string path, uint declared, bool partialFrame)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var value = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, declared);
        stream.Position = 4;
        stream.Write(value);
        stream.Position = 40;
        stream.Write(value);
        if (partialFrame)
        {
            stream.Seek(0, SeekOrigin.End);
            stream.WriteByte(1);
        }
    }

    private static MeetingJob Draft(string queue, double? seconds, MeetingJobPhase? phase = MeetingJobPhase.Recording)
    {
        var job = new MeetingJob(Support.SampleDate, 0, AppLanguage.German, 3, phase: phase);
        var folder = MeetingJob.Folder(job.Id, queue);
        Directory.CreateDirectory(folder);
        job.Write(queue);
        if (seconds is { } s) WriteWav(Path.Combine(folder, MeetingJob.AudioFileName), s);
        return job;
    }

    [Fact(DisplayName = "Repariert Kopf mit Größe 0 und schneidet ein angefangenes Frame ab")]
    public void RepairsZeroHeader()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "b.wav");
        WriteWav(path, 2.5);
        BreakHeader(path, 0, partialFrame: true);
        Assert.False(WavHeader.Inspect(path).IsConsistent);

        var info = WavHeader.Repair(path);
        Assert.Equal(40_000, info.Frames);
        var reread = WavHeader.Inspect(path);
        Assert.True(reread.IsConsistent);
        Assert.Equal(2.5, reread.DurationSeconds);
        Assert.Equal(WavHeader.CanonicalHeaderSize + 40_000 * 2, new FileInfo(path).Length);
    }

    [Fact(DisplayName = "Repariert einen veralteten Kopf (letzter Flush vor dem Absturz), lässt gültige Dateien unberührt")]
    public void RepairsStaleHeader()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "c.wav");
        WriteWav(path, 3);
        var written = File.GetLastWriteTimeUtc(path);
        Assert.Equal(3, WavHeader.Repair(path).DurationSeconds);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        BreakHeader(path, Rate * 2, partialFrame: false);
        Assert.Equal(3, WavHeader.Repair(path).DurationSeconds);
    }

    [Fact(DisplayName = "Liest Köpfe mit zusätzlichen Abschnitten (JUNK, fact) und erkennt Nicht-WAVs")]
    public void ParsesExtraChunks()
    {
        var head = new MemoryStream();
        void Ascii(string s) => head.Write(Encoding.ASCII.GetBytes(s));
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); head.Write(b); }
        void U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); head.Write(b); }
        Ascii("RIFF"); U32(0); Ascii("WAVE");
        Ascii("JUNK"); U32(28); head.Write(new byte[28]);
        Ascii("fmt "); U32(18); U16(3); U16(1); U32(16_000); U32(64_000); U16(4); U16(32); U16(0);
        Ascii("fact"); U32(4); U32(0);
        Ascii("data"); U32(0);
        var bytes = head.ToArray();
        var info = WavHeader.Parse(bytes, bytes.Length + 64_000);
        Assert.Equal(bytes.Length, info.DataOffset);
        Assert.Equal(16_000, info.Frames);
        Assert.False(info.IsConsistent);
        Assert.Throws<WavHeader.FormatException>(() => WavHeader.Parse(Encoding.UTF8.GetBytes("hallo welt, keine wav"), 21));
    }

    [Fact(DisplayName = "Verwaister Entwurf wird gerettet, nicht verarbeitet und nicht gelöscht")]
    public void RecoversOrphanedDraft()
    {
        using var temp = new TempFolder();
        var job = Draft(temp.Path, 4);
        var folder = MeetingJob.Folder(job.Id, temp.Path);
        BreakHeader(Path.Combine(folder, MeetingJob.AudioFileName), 0, partialFrame: true);
        File.WriteAllBytes(Path.Combine(folder, MeetingJob.LockFileName), Array.Empty<byte>());

        var report = RecordingRecovery.Run(temp.Path);
        Assert.Single(report.Recovered);
        Assert.Equal(0, report.DiscardedEmpty);
        Assert.Equal(0, report.SkippedActive);
        var stored = MeetingJob.Read(folder);
        Assert.Equal(MeetingJobPhase.Interrupted, stored.Phase);
        Assert.Equal(4, stored.DurationSeconds);
        Assert.Equal(3, stored.ExpectedSpeakers);
        Assert.Equal(AppLanguage.German, stored.Language);
        Assert.False(File.Exists(Path.Combine(folder, MeetingJob.LockFileName)));
        Assert.True(WavHeader.Inspect(Path.Combine(folder, MeetingJob.AudioFileName)).IsConsistent);
        // Zweiter Lauf ändert nichts mehr.
        Assert.Empty(RecordingRecovery.Run(temp.Path).Recovered);
        Assert.Equal(new MeetingJobPhase?[] { MeetingJobPhase.Interrupted }, MeetingJob.ReadAll(temp.Path).Select(j => j.Phase));
    }

    [Fact(DisplayName = "Laufende Aufnahme (Sperre gehalten) bleibt unberührt")]
    public void SkipsActiveRecording()
    {
        using var temp = new TempFolder();
        var job = Draft(temp.Path, 2);
        var folder = MeetingJob.Folder(job.Id, temp.Path);
        var lockPath = Path.Combine(folder, MeetingJob.LockFileName);
        var held = RecordingLock.Acquire(lockPath);
        Assert.NotNull(held);
        Assert.True(job.IsRecordingActive(temp.Path));
        Assert.Null(RecordingLock.Acquire(lockPath));

        var report = RecordingRecovery.Run(temp.Path);
        Assert.Equal(1, report.SkippedActive);
        Assert.Empty(report.Recovered);
        Assert.Equal(MeetingJobPhase.Recording, MeetingJob.Read(folder).Phase);

        held!.Release();
        Assert.False(job.IsRecordingActive(temp.Path));
        Assert.Single(RecordingRecovery.Run(temp.Path).Recovered);
    }

    [Fact(DisplayName = "Leere und sehr kurze Entwürfe werden still verworfen, beendete Aufträge nicht angefasst")]
    public void DiscardsEmptyDrafts()
    {
        using var temp = new TempFolder();
        var shortDraft = Draft(temp.Path, 0.4);
        var noAudio = Draft(temp.Path, null);
        var headerOnly = Draft(temp.Path, null);
        File.WriteAllBytes(Path.Combine(MeetingJob.Folder(headerOnly.Id, temp.Path), MeetingJob.AudioFileName), WavHeader.Pcm16Header(Rate, 1, 0));
        var waiting = Draft(temp.Path, 0.4, phase: null);

        var report = RecordingRecovery.Run(temp.Path);
        Assert.Equal(3, report.DiscardedEmpty);
        Assert.Empty(report.Recovered);
        foreach (var job in new[] { shortDraft, noAudio, headerOnly })
            Assert.False(Directory.Exists(MeetingJob.Folder(job.Id, temp.Path)));
        Assert.Equal(new[] { waiting.Id }, MeetingJob.ReadAll(temp.Path).Select(j => j.Id));
    }

    [Fact(DisplayName = "Lose WAVs der alten Ablage werden als gerettete Aufträge übernommen")]
    public void ImportsLegacyRecordings()
    {
        using var queue = new TempFolder();
        using var legacy = new TempFolder();
        var file = Path.Combine(legacy.Path, Guid.NewGuid().ToString("D").ToUpperInvariant() + ".wav");
        WriteWav(file, 2);
        BreakHeader(file, 0, partialFrame: false);
        WriteWav(Path.Combine(legacy.Path, "kurz.wav"), 0.2);
        // Frisch geänderte Dateien gelten als noch in Arbeit (alte Version läuft womöglich parallel).
        Assert.Equal(2, RecordingRecovery.Run(queue.Path, legacy.Path).SkippedActive);
        var past = DateTime.UtcNow - RecordingRecovery.LegacyQuietPeriod - TimeSpan.FromSeconds(5);
        foreach (var f in Directory.GetFiles(legacy.Path)) File.SetLastWriteTimeUtc(f, past);

        var report = RecordingRecovery.Run(queue.Path, legacy.Path, AppLanguage.English);
        Assert.Single(report.Recovered);
        Assert.Equal(1, report.DiscardedEmpty);
        var job = Assert.Single(MeetingJob.ReadAll(queue.Path));
        Assert.Equal(MeetingJobPhase.Interrupted, job.Phase);
        Assert.Equal(AppLanguage.English, job.Language);
        Assert.Equal(2, job.DurationSeconds);
        Assert.Empty(Directory.GetFiles(legacy.Path));
    }

    [Fact(DisplayName = "job.json ohne neue Felder (bis 1.1.0) liest sich als wartender Auftrag")]
    public void DecodesLegacyJob()
    {
        const string json = """
            {"durationSeconds":612.5,"expectedSpeakers":2,"id":"6B1E2B0A-6E0E-4C59-8C55-2D8C1D8E2F11",
             "language":"de","recordedAt":"2026-09-30T08:15:00Z"}
            """;
        var job = JsonCoding.Decode<MeetingJob>(json);
        Assert.Null(job.Phase);
        Assert.Null(job.Failure);
        Assert.Equal(612.5, job.DurationSeconds);
        Assert.Equal(2, job.ExpectedSpeakers);
        Assert.DoesNotContain("phase", JsonCoding.Encode(job));

        var draft = job with { Phase = MeetingJobPhase.Recording };
        Assert.Contains("\"phase\" : \"recording\"", JsonCoding.Encode(draft));
        Assert.Equal(draft, JsonCoding.Decode<MeetingJob>(JsonCoding.Encode(draft)));
        var interrupted = job with { Phase = MeetingJobPhase.Interrupted };
        Assert.Equal(interrupted, JsonCoding.Decode<MeetingJob>(JsonCoding.EncodeToUtf8(interrupted)));
    }
}
