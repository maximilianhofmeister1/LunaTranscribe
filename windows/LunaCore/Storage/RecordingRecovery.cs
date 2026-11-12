using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LunaCore;

/// <summary>
/// Rettet Meeting-Aufnahmen, die beim letzten Lauf nicht regulär beendet wurden (Absturz, Task beenden, Herunterfahren
/// mit laufendem Meeting). Läuft beim Start der App, bevor die Warteschlange ihre Aufträge einliest (Port von
/// <c>RecordingRecovery.swift</c>).
///
/// Ein Auftrag in Phase Recording, dessen <see cref="RecordingLock"/> niemand mehr hält, ist verwaist: Die WAV wird
/// repariert, die Dauer aus der Dateigröße bestimmt und der Auftrag als Interrupted markiert – verarbeitet wird erst
/// auf Klick des Nutzers. Gelöscht werden nur Aufträge ohne nennenswertes Audio. Hält jemand die Sperre (zweite
/// Instanz, Selbsttest), bleibt der Auftrag unberührt.
/// </summary>
public static class RecordingRecovery
{
    public sealed class Report
    {
        /// <summary>Gerettete Aufträge (jetzt Interrupted), älteste zuerst.</summary>
        public List<MeetingJob> Recovered { get; } = new();
        /// <summary>Ohne nennenswertes Audio still entfernt.</summary>
        public int DiscardedEmpty { get; set; }
        /// <summary>Aufnahme läuft noch in einem anderen Prozess (bzw. Altlast frisch geändert).</summary>
        public int SkippedActive { get; set; }
        /// <summary>Kopf nicht reparierbar; Auftrag bleibt trotzdem erhalten (Verarbeitung meldet dann einen Fehler).</summary>
        public List<string> Unrepairable { get; } = new();
    }

    /// <summary>Kürzere Entwürfe enthalten nichts Verwertbares und werden still verworfen.</summary>
    public const double MinimumDuration = 1;

    /// <summary>
    /// Lose WAVs der alten Ablage, die so kürzlich geändert wurden, gelten als noch in Arbeit – eine alte Version
    /// (ohne Sperre) könnte parallel laufen und gerade hineinschreiben.
    /// </summary>
    public static readonly TimeSpan LegacyQuietPeriod = TimeSpan.FromSeconds(60);

    /// <param name="queueFolder">Warteschlangen-Ordner.</param>
    /// <param name="legacyFolder">
    /// Ordner der alten Aufnahmen-Ablage (bis 1.1.0: <see cref="AppPaths.MeetingRecordings"/>, lose WAVs ohne Auftrag).
    /// Dort liegende Reste werden repariert und als gerettete Aufträge übernommen.
    /// </param>
    /// <param name="legacyLanguage">Sprache für diese Altlasten (sie haben keine Metadaten).</param>
    public static Report Run(string queueFolder, string? legacyFolder = null, AppLanguage legacyLanguage = AppLanguage.German)
    {
        var report = new Report();
        string[] folders;
        try { folders = Directory.GetDirectories(queueFolder); }
        catch (Exception) { folders = Array.Empty<string>(); }

        foreach (var folder in folders)
        {
            MeetingJob job;
            try { job = MeetingJob.Read(folder); }
            catch (Exception) { continue; }
            var lockPath = Path.Combine(folder, MeetingJob.LockFileName);
            if (job.Phase != MeetingJobPhase.Recording)
            {
                // Übrig gebliebene Sperrdatei eines regulär beendeten Auftrags (Absturz zwischen zwei Schritten).
                if (File.Exists(lockPath) && !RecordingLock.IsHeld(lockPath)) TryDeleteFile(lockPath);
                continue;
            }
            using var held = RecordingLock.Acquire(lockPath);
            if (held == null)
            {
                report.SkippedActive++;
                continue;
            }

            var audio = Path.Combine(folder, MeetingJob.AudioFileName);
            var size = File.Exists(audio) ? new FileInfo(audio).Length : 0;
            double duration;
            try
            {
                var info = WavHeader.Repair(audio);
                if (info.DurationSeconds < MinimumDuration)
                {
                    held.Release();
                    TryDeleteFolder(folder);
                    report.DiscardedEmpty++;
                    continue;
                }
                duration = info.DurationSeconds;
            }
            catch (Exception)
            {
                // Kein Audio jenseits des Kopfes: nichts zu retten. Sonst behalten – lieber ein Auftrag, der beim
                // Verarbeiten scheitert, als eine gelöschte Stunde Meeting.
                if (size <= WavHeader.CanonicalHeaderSize)
                {
                    held.Release();
                    TryDeleteFolder(folder);
                    report.DiscardedEmpty++;
                    continue;
                }
                duration = EstimatedDuration(size);
                report.Unrepairable.Add(Path.GetFileName(folder));
            }

            var recovered = job with { DurationSeconds = duration, Phase = MeetingJobPhase.Interrupted };
            try
            {
                recovered.Write(queueFolder);
                held.Release(removeFile: true);
                report.Recovered.Add(recovered);
            }
            catch (Exception)
            {
                // Bleibt Recording und wird beim nächsten Start erneut versucht.
                report.Unrepairable.Add(Path.GetFileName(folder));
            }
        }
        if (legacyFolder != null) ImportLegacy(legacyFolder, queueFolder, legacyLanguage, report);
        report.Recovered.Sort((a, b) => a.RecordedAt.CompareTo(b.RecordedAt));
        return report;
    }

    /// <summary>Lose WAVs der alten Ablage als gerettete Aufträge übernehmen. Unlesbare bleiben liegen.</summary>
    private static void ImportLegacy(string folder, string queueFolder, AppLanguage language, Report report)
    {
        string[] files;
        try { files = Directory.GetFiles(folder, "*.wav"); }
        catch (Exception) { return; }

        foreach (var file in files)
        {
            var modified = File.GetLastWriteTimeUtc(file);
            if (DateTime.UtcNow - modified < LegacyQuietPeriod)
            {
                report.SkippedActive++;
                continue;
            }
            WavHeader.Info info;
            try { info = WavHeader.Repair(file); }
            catch (Exception)
            {
                report.Unrepairable.Add(Path.GetFileName(file));
                continue;
            }
            if (info.DurationSeconds < MinimumDuration)
            {
                TryDeleteFile(file);
                report.DiscardedEmpty++;
                continue;
            }
            var created = File.GetCreationTimeUtc(file);
            var recordedAt = created > DateTime.UnixEpoch ? created : modified.AddSeconds(-info.DurationSeconds);
            var job = new MeetingJob(recordedAt, info.DurationSeconds, language, null, phase: MeetingJobPhase.Interrupted);
            var target = MeetingJob.Folder(job.Id, queueFolder);
            try
            {
                // Erst der Auftrag, dann die Datei: Scheitert das Verschieben, bleibt sie in der alten Ablage.
                Directory.CreateDirectory(target);
                job.Write(queueFolder);
                File.Move(file, Path.Combine(target, MeetingJob.AudioFileName));
                report.Recovered.Add(job);
            }
            catch (Exception)
            {
                TryDeleteFolder(target);
                report.Unrepairable.Add(Path.GetFileName(file));
            }
        }
    }

    /// <summary>Dauer, falls der Kopf unlesbar ist: Annahme 16 kHz mono PCM16 (so nimmt die Windows-Version auf).</summary>
    internal static double EstimatedDuration(long bytes) => Math.Max(0, bytes - WavHeader.CanonicalHeaderSize) / (16_000.0 * 2);

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* best effort */ }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (Exception) { /* best effort */ }
    }
}
