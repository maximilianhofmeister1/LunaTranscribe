using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.Settings;
using NAudio.Wave;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Entwickler-Einstieg für die Rettung abgebrochener Meeting-Aufnahmen – ohne Mikrofon, direkt aus dem Terminal
/// (Pendant zu RecoverySelfTest.swift):
/// <code>LunaTranscribe.exe --recovery-selftest</code>
/// Baut in einem temporären Ordner nach, was ein Absturz mitten im Meeting hinterlässt: einen Entwurf mit veraltetem
/// WAV-Kopf und angefangenem Frame (wie NAudio ihn nach dem letzten Kopf-Flush zurücklässt), eine lose WAV der alten
/// Ablage mit Kopfgröße 0, einen sehr kurzen Entwurf und eine noch laufende Aufnahme (Sperre gehalten). Dann startet eine
/// echte <see cref="MeetingProcessingQueue"/> auf diesem Ordner und es wird geprüft: Dauer aus der Dateigröße, WAV mit
/// NAudio lesbar und inhaltlich unverändert, nichts automatisch verarbeitet, die laufende Aufnahme unberührt,
/// „Transkription starten“ und der reguläre Ablauf Start → Beenden.
/// </summary>
public static class RecoverySelfTest
{
    private const int Rate = 16_000;

    [SelfTest("--recovery-selftest")]
    private static Task<int> Run(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "luna-recovery-" + Guid.NewGuid().ToString("N"));
        var models = new ModelManager(Application.Current.Dispatcher);
        try
        {
            var failures = RunChecks(root, models);
            SelfTests.Print(failures == 0 ? "\nAlle Prüfungen bestanden." : $"\n{failures} Prüfung(en) fehlgeschlagen.");
            return Task.FromResult(failures == 0 ? 0 : 1);
        }
        finally
        {
            models.Shutdown();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static int RunChecks(string root, ModelManager models)
    {
        var failures = 0;
        void Check(bool ok, string label)
        {
            SelfTests.Print($"{(ok ? "OK    " : "FEHLER")} {label}");
            if (!ok) failures++;
        }

        var queueFolder = Path.Combine(root, "Queue");
        var legacyFolder = Path.Combine(root, "Recordings");
        Directory.CreateDirectory(queueFolder);
        Directory.CreateDirectory(legacyFolder);
        var settings = SettingsStore.Shared;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            // 1. Absturz mitten in der Aufnahme: letzter Kopf-Flush nach 3 s, danach weitere 2,3 s + halbes Frame.
            var crashed = MakeDraft(queueFolder);
            var crashedAudio = Path.Combine(MeetingJob.Folder(crashed.Id, queueFolder), MeetingJob.AudioFileName);
            WriteWav(crashedAudio, 5 * Rate + Rate * 3 / 10);
            SetSizes(crashedAudio, 3 * Rate * 2);
            using (var append = new FileStream(crashedAudio, FileMode.Append)) append.WriteByte(0x12);
            var broken = WavHeader.Inspect(crashedAudio);
            Check(Math.Abs(broken.DurationSeconds - 5.3) < 0.001 && broken.DeclaredDataBytes == 3 * Rate * 2,
                $"Kopf nach Absturz veraltet ({broken.DeclaredDataBytes} Bytes laut Kopf, {broken.AvailableDataBytes} vorhanden)");

            // 2. Lose WAV der alten Ablage (bis 1.1.0) mit Kopfgröße 0, seit über einer Minute unverändert.
            var legacyFile = Path.Combine(legacyFolder, Guid.NewGuid().ToString("D").ToUpperInvariant() + ".wav");
            WriteWav(legacyFile, 4 * Rate);
            SetSizes(legacyFile, 0);
            File.SetLastWriteTimeUtc(legacyFile, DateTime.UtcNow.AddMinutes(-5));

            // 3. Sehr kurzer Entwurf (0,4 s) – wird still verworfen.
            var shortDraft = MakeDraft(queueFolder);
            WriteWav(Path.Combine(MeetingJob.Folder(shortDraft.Id, queueFolder), MeetingJob.AudioFileName), Rate * 4 / 10);

            // 4. Laufende Aufnahme in einem „anderen Prozess“: Sperre gehalten.
            var active = MakeDraft(queueFolder);
            var activeFolder = MeetingJob.Folder(active.Id, queueFolder);
            WriteWav(Path.Combine(activeFolder, MeetingJob.AudioFileName), 3 * Rate);
            var activeLock = RecordingLock.Acquire(Path.Combine(activeFolder, MeetingJob.LockFileName));
            Check(activeLock != null, "Sperre der laufenden Aufnahme gehalten");

            var t0 = Stopwatch.StartNew();
            var store = new MeetingStore(settings, dispatcher, Path.Combine(root, "Bibliothek"), Path.Combine(root, "Unsaved"));
            var queue = new MeetingProcessingQueue(settings, models, store, new FakeMeetingPipeline(), queueFolder, legacyFolder);
            SelfTests.Print($"       Rettung beim Start dauerte {t0.ElapsedMilliseconds} ms");
            var interrupted = queue.Items.Where(i => i.IsInterrupted).ToList();
            Check(queue.RecoveredAtLaunch.Count == 2 && interrupted.Count == 2 && queue.Items.Count == 2,
                $"zwei Aufnahmen gerettet und als unterbrochen gelistet ({queue.RecoveredAtLaunch.Count}/{interrupted.Count}/{queue.Items.Count})");
            Check(queue.PendingCount == 0 && !queue.IsProcessing, "nichts automatisch verarbeitet");
            Check(!Directory.Exists(MeetingJob.Folder(shortDraft.Id, queueFolder)), "kurzer Entwurf verworfen");
            Check(MeetingJob.Read(activeFolder).Phase == MeetingJobPhase.Recording, "laufende Aufnahme unberührt");
            Check(Directory.GetFiles(legacyFolder).Length == 0, "alte Ablage übernommen");

            if (queue.Items.FirstOrDefault(i => i.Id == crashed.Id) is { } item)
            {
                Check(Math.Abs(item.Job.DurationSeconds - 5.3) < 0.001, $"Dauer aus Dateigröße: {item.Job.DurationSeconds} s");
                Check(VerifyReadable(crashedAudio, 5 * Rate + Rate * 3 / 10), "repariertes WAV mit NAudio lesbar, Inhalt unverändert");
                Check(item.Job.ExpectedSpeakers == 2 && item.Job.Language == AppLanguage.German, "Metadaten vom Start erhalten");
            }
            else Check(false, "Entwurf nach Absturz in der Warteschlange");

            if (queue.Items.FirstOrDefault(i => i.Id != crashed.Id) is { } legacy)
            {
                Check(Math.Abs(legacy.Job.DurationSeconds - 4) < 0.001, $"alte Aufnahme: Dauer {legacy.Job.DurationSeconds} s");
                Check(VerifyReadable(Path.Combine(MeetingJob.Folder(legacy.Id, queueFolder), MeetingJob.AudioFileName), 4 * Rate),
                    "alte Aufnahme lesbar");
            }

            // 5. Der andere Prozess endet (Sperre frei) → nächste Suche rettet auch diese Aufnahme.
            activeLock?.Release();
            var later = queue.RecoverInterruptedRecordings();
            Check(later.Select(j => j.Id).SequenceEqual(new[] { active.Id }) && queue.InterruptedCount == 3, "Aufnahme nach Prozessende gerettet");
            Check(later.FirstOrDefault() is { } l && Math.Abs(l.DurationSeconds - 3) < 0.001, "Dauer inkl. Daten nach dem letzten Flush");

            // 6. „Transkription starten“ (Warteschlange inaktiv: der Auftrag bleibt wartend).
            queue.Retry(crashed.Id);
            Check(queue.Items.FirstOrDefault(i => i.Id == crashed.Id)?.Phase == QueueItemPhase.Waiting, "Transkription starten → wartet");
            Check(MeetingJob.Read(MeetingJob.Folder(crashed.Id, queueFolder)).Phase == null, "job.json: Phase entfernt");

            // 7. Regulärer Ablauf: Start → Beenden.
            var recording = queue.BeginRecording(DateTime.UtcNow, AppLanguage.German, 0);
            WriteWav(recording.AudioPath, 2 * Rate);
            Check(MeetingJob.ReadAll(queueFolder).FirstOrDefault(j => j.Id == recording.Job.Id)?.IsRecordingActive(queueFolder) == true,
                "laufende Aufnahme hält ihre Sperre");
            Check(queue.RecoverInterruptedRecordings().Count == 0, "eigene laufende Aufnahme wird nicht für verwaist gehalten");
            queue.FinishRecording(recording, 2, AppLanguage.English, 3);
            var recordingFolder = MeetingJob.Folder(recording.Job.Id, queueFolder);
            var finished = MeetingJob.Read(recordingFolder);
            Check(finished.Phase == null && finished.Language == AppLanguage.English && finished.ExpectedSpeakers == 3,
                "Beenden: Auftrag wartet mit Sprache und Sprecherzahl vom Ende");
            Check(!File.Exists(Path.Combine(recordingFolder, MeetingJob.LockFileName)), "Sperrdatei entfernt");
            Check(WavHeader.Inspect(recording.AudioPath).IsConsistent, "WAV nach regulärem Beenden vollständig");

            var cancelled = queue.BeginRecording(DateTime.UtcNow, AppLanguage.German, 0);
            queue.DiscardRecording(cancelled);
            Check(!Directory.Exists(MeetingJob.Folder(cancelled.Job.Id, queueFolder)), "Verwerfen entfernt den Entwurf");
        }
        catch (Exception ex)
        {
            Check(false, "Unerwarteter Fehler: " + ex.Message);
        }
        return failures;
    }

    private static MeetingJob MakeDraft(string queueFolder)
    {
        var job = new MeetingJob(DateTime.UtcNow.AddMinutes(-30), 0, AppLanguage.German, 2, phase: MeetingJobPhase.Recording);
        Directory.CreateDirectory(MeetingJob.Folder(job.Id, queueFolder));
        job.Write(queueFolder);
        return job;
    }

    private static short Sample(int i) => (short)(Math.Sin(i * 0.031) * 9000);

    /// <summary>Schreibt wie die App mit NAudio (16 kHz, mono, PCM16) ein erkennbares Signal.</summary>
    private static void WriteWav(string path, int frames)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1));
        var buffer = new byte[frames * 2];
        for (var i = 0; i < frames; i++) BitConverter.TryWriteBytes(buffer.AsSpan(i * 2), Sample(i));
        writer.Write(buffer, 0, buffer.Length);
    }

    /// <summary>Simuliert einen veralteten Kopf: RIFF- und data-Größe so, als wäre nur <paramref name="dataBytes"/> geschrieben.</summary>
    private static void SetSizes(string path, uint dataBytes)
    {
        var info = WavHeader.Inspect(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Position = 4;
        stream.Write(BitConverter.GetBytes(dataBytes == 0 ? 0u : (uint)(info.DataOffset - 8 + dataBytes)));
        stream.Position = info.DataOffset - 4;
        stream.Write(BitConverter.GetBytes(dataBytes));
    }

    private static bool VerifyReadable(string path, int frames)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var buffer = new byte[reader.Length];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read / 2 != frames)
            {
                SelfTests.Print($"       gelesen: {read / 2} Frames, erwartet {frames}");
                return false;
            }
            for (var i = 0; i < frames; i++)
                if (BitConverter.ToInt16(buffer, i * 2) != Sample(i)) return false;
            return true;
        }
        catch (Exception ex)
        {
            SelfTests.Print("       nicht lesbar: " + ex.Message);
            return false;
        }
    }
}
