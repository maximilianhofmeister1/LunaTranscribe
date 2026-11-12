using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Meeting;

public enum QueueItemPhase
{
    Waiting,
    Processing,
    Failed,
    /// <summary>Gerettete Aufnahme nach Absturz/Herunterfahren; wartet auf „Transkription starten“.</summary>
    Interrupted,
}

/// <summary>Ein Auftrag in der Warteschlange mit seinem Zustand (unveränderlich; die Warteschlange ersetzt ihn).</summary>
public sealed record QueueItem(MeetingJob Job, QueueItemPhase Phase, string Step = "", double Fraction = 0, string? Failure = null)
{
    public Guid Id => Job.Id;
    public bool IsProcessing => Phase == QueueItemPhase.Processing;
    public bool IsFailed => Phase == QueueItemPhase.Failed;
    public bool IsInterrupted => Phase == QueueItemPhase.Interrupted;
}

/// <summary>Eine laufende Aufnahme: Auftragsordner mit <c>job.json</c> (Phase Recording) und gehaltener Sperre.</summary>
public sealed class MeetingRecordingDraft
{
    public MeetingJob Job { get; }
    public string AudioPath { get; }
    internal RecordingLock Lock { get; }

    internal MeetingRecordingDraft(MeetingJob job, string audioPath, RecordingLock recordingLock)
    {
        Job = job;
        AudioPath = audioPath;
        Lock = recordingLock;
    }
}

/// <summary>
/// Verarbeitet beendete Meetings im Hintergrund, eines nach dem anderen: Sprecher erkennen, transkribieren, in der
/// Bibliothek ablegen (über <see cref="IMeetingPipeline"/>). Die Aufnahme ist dadurch sofort frei für das nächste Meeting.
///
/// Nacheinander statt parallel, weil sich Sprechererkennung und Engine sonst um CPU und RAM streiten und beide
/// Meetings später fertig würden. Invariante: <b>eine Aufnahme geht nie verloren</b> – sie wird erst gelöscht, wenn
/// das Transkript gespeichert ist oder der Nutzer sie verwirft. Aufträge liegen als <c>Queue\&lt;id&gt;\audio.wav</c>
/// + <c>job.json</c> und überstehen einen Neustart.
///
/// Windows: Der <see cref="ModelManager"/> entlädt das Modell nach Inaktivität. Die Warteschlange lädt es deshalb
/// über <see cref="ModelManager.WaitForReadyAsync"/> selbst nach und meldet über <see cref="IsProcessing"/>, dass
/// es während eines Auftrags nicht entladen werden darf (<c>ModelManager.CanUnload</c> in AppState). Läuft auf dem UI-Thread.
///
/// Schon während der Aufnahme liegt das Meeting als Auftrag in Phase Recording hier (<see cref="BeginRecording"/>),
/// gesichert durch eine <see cref="RecordingLock"/>. Stürzt die App ab oder wird der PC mit laufendem Meeting
/// ausgeschaltet, findet <see cref="RecordingRecovery"/> den Auftrag beim nächsten Start, repariert die WAV und zeigt
/// ihn als „Unterbrochene Aufnahme“ – verarbeitet wird er erst auf Klick.
/// </summary>
public sealed class MeetingProcessingQueue : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fortschritt eines laufenden Auftrags (häufig; ändert die Liste nicht).</summary>
    public event Action<QueueItem>? ProgressChanged;

    /// <summary>Ein fertig gespeichertes Meeting (Titel, Bibliotheks-ID) – für die Mitteilung.</summary>
    public event Action<string, Guid>? Finished;

    /// <summary>Ein fehlgeschlagener Auftrag (Titel, Meldung) – für die Mitteilung.</summary>
    public event Action<string, string>? Failed;

    private readonly SettingsStore _settings;
    private readonly ModelManager _models;
    private readonly MeetingStore _store;
    private IMeetingPipeline _pipeline;
    private List<QueueItem> _items = new();
    private Task? _running;
    /// <summary>Die Pipeline auf dem Thread-Pool (ohne UI-Fortsetzung) – darauf wartet <see cref="Shutdown"/>.</summary>
    private Task? _pipelineTask;
    private CancellationTokenSource? _runningCts;
    private Guid? _runningId;
    private bool _active;
    private readonly string _queueFolder;

    /// <param name="queueFolder">Nur Selbsttests: Warteschlangen-Ordner statt <see cref="AppPaths.MeetingQueue"/>.</param>
    /// <param name="legacyRecordings">
    /// Alte Aufnahmen-Ablage (bis 1.1.0), deren Reste beim Start gerettet werden. Ohne Angabe bei der echten Warteschlange
    /// <see cref="AppPaths.MeetingRecordings"/>, bei einer Selbsttest-Warteschlange keine.
    /// </param>
    public MeetingProcessingQueue(SettingsStore settings, ModelManager models, MeetingStore store, IMeetingPipeline pipeline,
                                  string? queueFolder = null, string? legacyRecordings = null)
    {
        _settings = settings;
        _models = models;
        _store = store;
        _pipeline = pipeline;
        _queueFolder = queueFolder ?? AppPaths.MeetingQueue;
        EngineProvider = ct => _models.WaitForReadyAsync(_settings.Engine, ct);
        Restore(legacyRecordings ?? (queueFolder == null ? AppPaths.MeetingRecordings : null));
        // Beim Start ist die Engine meist noch nicht geladen – sobald sie bereit ist, weitermachen.
        _models.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelManager.State) && _models.IsReady) ProcessNext();
        };
    }

    /// <summary>
    /// Verarbeitung erlaubt. false in Selbsttests ohne Modelle (UI-Snapshots): Aufträge bleiben dann einfach wartend.
    /// Beim Einschalten startet ein wartender Auftrag sofort (und lädt das Modell bei Bedarf nach).
    /// </summary>
    public bool Active
    {
        get => _active;
        set { _active = value; if (value) ProcessNext(); }
    }

    /// <summary>Die Verarbeitung (Sprechererkennung + Transkription); austauschbar für Selbsttests.</summary>
    public IMeetingPipeline Pipeline
    {
        get => _pipeline;
        set => _pipeline = value;
    }

    /// <summary>
    /// Liefert die geladene Engine; lädt das Modell nach, falls es nach Inaktivität entladen wurde.
    /// Selbsttests mit der Fake-Pipeline ersetzen das, damit kein Modell geladen wird.
    /// </summary>
    internal Func<CancellationToken, Task<ITranscriptionEngine>> EngineProvider { get; set; }

    /// <summary>Älteste zuerst.</summary>
    public IReadOnlyList<QueueItem> Items => _items;

    /// <summary>Läuft gerade eine Verarbeitung? Solange darf das Modell nicht entladen werden.</summary>
    public bool IsProcessing => _running != null;

    /// <summary>Aufträge, die noch verarbeitet werden (laufend oder wartend).</summary>
    public int PendingCount => _items.Count(i => i.Phase is QueueItemPhase.Waiting or QueueItemPhase.Processing);

    /// <summary>Gerettete Aufnahmen, die auf eine Entscheidung warten.</summary>
    public int InterruptedCount => _items.Count(i => i.IsInterrupted);

    /// <summary>Beim Start gerettete, unterbrochene Aufnahmen (für die Mitteilung).</summary>
    public IReadOnlyList<MeetingJob> RecoveredAtLaunch { get; private set; } = Array.Empty<MeetingJob>();

    /// <summary>Ordner der Warteschlange (Selbsttests nutzen einen temporären).</summary>
    public string Folder => _queueFolder;

    // MARK: Aufnahme

    /// <summary>
    /// Legt den Auftrag für ein neues Meeting an, bevor das erste Sample geschrieben wird: Ordner, Sperre, <c>job.json</c>
    /// in Phase Recording. Die Aufnahme schreibt direkt nach <see cref="MeetingRecordingDraft.AudioPath"/>.
    /// </summary>
    /// <param name="expectedSpeakers">Bekannte Teilnehmerzahl, 0 = automatisch.</param>
    public MeetingRecordingDraft BeginRecording(DateTime recordedAt, AppLanguage language, int expectedSpeakers)
    {
        var job = new MeetingJob(recordedAt, 0, language, expectedSpeakers > 0 ? expectedSpeakers : null, phase: MeetingJobPhase.Recording);
        var folder = FolderFor(job.Id);
        Directory.CreateDirectory(folder);
        // Erst die Sperre, dann job.json – sonst könnte eine zweite Instanz den Entwurf für verwaist halten.
        var recordingLock = RecordingLock.Acquire(Path.Combine(folder, MeetingJob.LockFileName));
        if (recordingLock == null)
        {
            DeleteFolder(folder);
            throw new IOException("Die Aufnahme konnte nicht vorbereitet werden (Sperrdatei nicht verfügbar).");
        }
        try
        {
            job.Write(_queueFolder);
        }
        catch
        {
            recordingLock.Release();
            DeleteFolder(folder);
            throw;
        }
        return new MeetingRecordingDraft(job, Path.Combine(folder, MeetingJob.AudioFileName), recordingLock);
    }

    /// <summary>
    /// Aufnahme ist beendet: Auftrag freigeben und einreihen. Sprache und Sprecherzahl werden jetzt festgelegt.
    /// Scheitert das Schreiben, bleibt die Aufnahme als unterbrochene erhalten – nie verloren.
    /// </summary>
    public void FinishRecording(MeetingRecordingDraft draft, double durationSeconds, AppLanguage language, int expectedSpeakers)
    {
        var job = draft.Job with
        {
            DurationSeconds = durationSeconds,
            Language = language,
            ExpectedSpeakers = expectedSpeakers > 0 ? expectedSpeakers : null,
            Phase = null,
        };
        try
        {
            job.Write(_queueFolder);
            draft.Lock.Release(removeFile: true);
            SetItems(_items.Append(new QueueItem(job, QueueItemPhase.Waiting)));
            Log.Write($"meeting queued: {durationSeconds:0} s, {_items.Count} in queue");
            ProcessNext();
        }
        catch (Exception ex)
        {
            // job.json sagt weiter „recording“, die Sperre ist frei → spätestens der nächste Start rettet sie.
            draft.Lock.Release();
            SetItems(_items.Append(new QueueItem(job with { Phase = MeetingJobPhase.Interrupted }, QueueItemPhase.Interrupted)));
            Log.Write("meeting could not be queued, kept as interrupted: " + ex.Message);
        }
    }

    /// <summary>Aufnahme abgebrochen (Nutzer oder kein Audio): Entwurf samt Datei entfernen.</summary>
    public void DiscardRecording(MeetingRecordingDraft draft)
    {
        draft.Lock.Release();
        DeleteFolder(FolderFor(draft.Job.Id));
    }

    // MARK: Aufträge

    /// <summary>
    /// Übernimmt eine fertige Aufnahme von außen (Selbsttests): Datei nach <c>Queue\&lt;id&gt;\audio.wav</c> verschieben,
    /// Auftrag sichern, loslegen. Wirft, wenn das nicht geht – die Aufnahme bleibt dann, wo sie war.
    /// </summary>
    /// <param name="expectedSpeakers">Bekannte Teilnehmerzahl, 0 = automatisch.</param>
    /// <returns>ID des Auftrags.</returns>
    public Guid Enqueue(string wavPath, DateTime recordedAt, double durationSeconds, AppLanguage language, int expectedSpeakers)
    {
        var job = new MeetingJob(recordedAt, durationSeconds, language, expectedSpeakers > 0 ? expectedSpeakers : null);
        var folder = FolderFor(job.Id);
        Directory.CreateDirectory(folder);
        try
        {
            File.Move(wavPath, Path.Combine(folder, MeetingJob.AudioFileName));
            job.Write(_queueFolder);
        }
        catch
        {
            // Aufnahme zurücklegen, falls sie schon verschoben war – lieber am alten Ort als gar nicht.
            var moved = Path.Combine(folder, MeetingJob.AudioFileName);
            if (File.Exists(moved) && !File.Exists(wavPath)) { try { File.Move(moved, wavPath); } catch { } }
            throw;
        }
        SetItems(_items.Append(new QueueItem(job, QueueItemPhase.Waiting)));
        Log.Write($"meeting queued: {durationSeconds:0} s, {_items.Count} in queue");
        ProcessNext();
        return job.Id;
    }

    /// <summary>Fehlgeschlagenen Auftrag erneut einreihen bzw. eine gerettete Aufnahme zur Transkription freigeben.</summary>
    public void Retry(Guid id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null || item.IsProcessing) return;
        var job = item.Job with { Failure = null, Phase = null };
        // Scheitert das Schreiben, läuft die Verarbeitung trotzdem; die Datei bleibt bis zum Speichern liegen.
        TryWrite(job);
        Replace(id, new QueueItem(job, QueueItemPhase.Waiting));
        ProcessNext();
    }

    /// <summary>Auftrag samt Aufnahme endgültig verwerfen – bricht eine laufende Verarbeitung ab.</summary>
    public void Discard(Guid id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        var folder = FolderFor(id);
        var running = _runningId == id ? _running : null;
        if (running != null)
        {
            _runningCts?.Cancel();
            ClearRunning();
        }
        SetItems(_items.Where(i => i.Id != id));
        if (running == null) DeleteFolder(folder);
        // Läuft die Pipeline noch auf der Datei, erst nach ihrem Ende löschen.
        else _ = running.ContinueWith(_ => DeleteFolder(folder), TaskScheduler.Default);
        Log.Write("meeting job discarded");
        ProcessNext();
    }

    /// <summary>
    /// Beim Beenden der App: laufenden Auftrag abbrechen. Er bleibt in der Warteschlange und läuft beim nächsten Start neu.
    /// Wartet kurz auf die Pipeline, damit das Modell nicht mitten in einer Dekodierung freigegeben wird.
    /// </summary>
    public void Shutdown()
    {
        _active = false;
        _runningCts?.Cancel();
        try { _pipelineTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception) { /* abgebrochen oder fehlgeschlagen – egal beim Beenden */ }
    }

    // MARK: Ablauf

    /// <summary>Startet den nächsten wartenden Auftrag, falls gerade keiner läuft.</summary>
    private void ProcessNext()
    {
        if (!_active || _running != null) return;
        var item = _items.FirstOrDefault(i => i.Phase == QueueItemPhase.Waiting);
        if (item == null) return;

        var step = _models.IsReady ? "Verarbeitung startet…" : "Modell wird geladen…";
        Replace(item.Id, item with { Phase = QueueItemPhase.Processing, Step = step, Fraction = 0 });
        var cts = new CancellationTokenSource();
        _runningCts = cts;
        _runningId = item.Id;
        _running = RunAsync(item.Job, cts.Token);
        OnPropertyChanged(nameof(IsProcessing));
    }

    private async Task RunAsync(MeetingJob job, CancellationToken ct)
    {
        await Task.Yield();   // _running ist gesetzt, bevor etwas passiert
        ITranscriptionEngine engine;
        try
        {
            engine = await EngineProvider(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Abandon(job);
            return;
        }
        catch (Exception ex)
        {
            // Modell nicht verfügbar: Auftrag wartet weiter und startet, sobald das Modell bereit ist.
            Log.Write("meeting queue waits for model: " + ex.Message);
            if (_runningId == job.Id)
            {
                ClearRunning();
                if (_items.FirstOrDefault(i => i.Id == job.Id) is { } it) Replace(job.Id, it with { Phase = QueueItemPhase.Waiting, Step = "", Fraction = 0 });
            }
            return;
        }

        _models.Touch();
        var audio = Path.Combine(FolderFor(job.Id), MeetingJob.AudioFileName);
        var progress = new Progress<(string Step, double Fraction)>(p => Update(job.Id, p.Step, p.Fraction));
        var pipeline = _pipeline;
        var started = DateTime.Now;
        try
        {
            var task = Task.Run(() => pipeline.RunAsync(audio, job.Language, job.ExpectedSpeakers ?? 0, engine, progress, ct), ct);
            _pipelineTask = task;
            var turns = await task;
            _models.Touch();
            if (ct.IsCancellationRequested) { Abandon(job); return; }
            Finish(job, turns, engine.Kind, DateTime.Now - started);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Abandon(job);
        }
        catch (Exception ex)
        {
            _models.Touch();
            Fail(job, ex is FileNotFoundException or DirectoryNotFoundException
                ? "Die Aufnahmedatei wurde nicht gefunden."
                : ex.Message);
        }
    }

    private void Finish(MeetingJob job, IReadOnlyList<MeetingTurnResult> results, EngineKind engineKind, TimeSpan elapsed)
    {
        if (_runningId != job.Id) return;   // inzwischen verworfen
        ClearRunning();
        var turns = BuildTurns(results);
        if (turns.Count == 0)
        {
            MarkFailed(job, "Im Meeting wurde keine Sprache erkannt.");
            ProcessNext();
            return;
        }
        var transcript = new MeetingTranscript(job.RecordedAt, job.DurationSeconds, engineKind, job.Language, turns);
        var folder = FolderFor(job.Id);
        var audio = Path.Combine(folder, MeetingJob.AudioFileName);
        // Mit „Aufnahme behalten“ zieht die WAV in den Meeting-Ordner, sonst wird sie mit dem Auftrag gelöscht.
        var entry = _store.Add(transcript, _settings.MeetingKeepAudio ? audio : null);
        if (entry != null)
        {
            DeleteFolder(folder);
            SetItems(_items.Where(i => i.Id != job.Id));
            Log.Write($"meeting processed in {elapsed.TotalSeconds:0.0}s: {turns.Count} turns, saved={Path.GetFileName(entry.Folder)}");
            Finished?.Invoke(entry.Title, entry.Id);
        }
        else
        {
            // Speichern ist gescheitert – Aufnahme behalten, damit ein neuer Versuch möglich ist.
            MarkFailed(job, "Das Transkript konnte nicht gespeichert werden. Meeting-Ordner prüfen und erneut versuchen.");
        }
        ProcessNext();
    }

    private void Fail(MeetingJob job, string message)
    {
        if (_runningId != job.Id) return;
        ClearRunning();
        MarkFailed(job, message);
        ProcessNext();
    }

    /// <summary>Abgebrochen (verworfen oder App beendet): nichts speichern, Aufnahme bleibt bzw. ist schon gelöscht.</summary>
    private void Abandon(MeetingJob job)
    {
        if (_runningId != job.Id) return;
        ClearRunning();
        if (_items.FirstOrDefault(i => i.Id == job.Id) is { } it) Replace(job.Id, it with { Phase = QueueItemPhase.Waiting, Step = "", Fraction = 0 });
    }

    private void MarkFailed(MeetingJob job, string message)
    {
        if (_items.FirstOrDefault(i => i.Id == job.Id) is not { } item) return;
        var failedJob = item.Job with { Failure = message };
        TryWrite(failedJob);
        Replace(job.Id, new QueueItem(failedJob, QueueItemPhase.Failed, Failure: message));
        Log.Write("meeting processing failed: " + message);
        Failed?.Invoke(job.Title, message);
    }

    /// <summary>Fortschritt übernehmen – nur solange der Auftrag noch läuft (verspätete Meldungen nach Abbruch ignorieren).</summary>
    private void Update(Guid id, string step, double fraction)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || !_items[index].IsProcessing || _runningId != id) return;
        var item = _items[index] with { Step = step, Fraction = Math.Clamp(fraction, 0, 1) };
        _items = _items.Select(i => i.Id == id ? item : i).ToList();
        ProgressChanged?.Invoke(item);
    }

    private void ClearRunning()
    {
        _running = null;
        _runningCts = null;
        _runningId = null;
        OnPropertyChanged(nameof(IsProcessing));
    }

    /// <summary>Sprecher nach erstem Auftreten als „Sprecher 1“, „Sprecher 2“ … benennen; leere Beiträge weglassen.</summary>
    internal static List<MeetingTranscript.Turn> BuildTurns(IReadOnlyList<MeetingTurnResult> results)
    {
        var mapping = new Dictionary<string, string>();
        var turns = new List<MeetingTranscript.Turn>();
        foreach (var r in results.OrderBy(r => r.Start))
        {
            var text = (r.Text ?? "").Trim();
            if (text.Length == 0) continue;
            if (!mapping.TryGetValue(r.SpeakerId, out var name))
            {
                name = $"Sprecher {mapping.Count + 1}";
                mapping[r.SpeakerId] = name;
            }
            turns.Add(new MeetingTranscript.Turn(name, r.Start, r.End, text));
        }
        return turns;
    }

    // MARK: Persistenz

    /// <summary>
    /// Aufträge vom letzten Lauf wieder aufnehmen. Ein beim Beenden laufender Auftrag wartet einfach erneut; eine beim
    /// Beenden laufende Aufnahme wird vorher gerettet.
    /// </summary>
    private void Restore(string? legacyRecordings)
    {
        RecoveredAtLaunch = RunRecovery(legacyRecordings).Recovered;
        var items = new List<QueueItem>();
        foreach (var job in MeetingJob.ReadAll(_queueFolder))
        {
            switch (job.Phase)
            {
                case MeetingJobPhase.Recording: continue;   // läuft gerade in einem anderen Prozess (Sperre gehalten)
                case MeetingJobPhase.Interrupted: items.Add(new QueueItem(job, QueueItemPhase.Interrupted)); break;
                default:
                    items.Add(job.Failure is { } f ? new QueueItem(job, QueueItemPhase.Failed, Failure: f) : new QueueItem(job, QueueItemPhase.Waiting));
                    break;
            }
        }
        _items = items;
        if (_items.Count > 0) Log.Write($"restored {_items.Count} queued meeting(s)");
    }

    /// <summary>
    /// Sucht verwaiste Aufnahmen (Sperre frei) und zeigt sie als unterbrochen an. Sicher jederzeit aufrufbar – die eigene
    /// laufende Aufnahme hält ihre Sperre und bleibt unberührt.
    /// </summary>
    public IReadOnlyList<MeetingJob> RecoverInterruptedRecordings()
    {
        var recovered = RunRecovery(null).Recovered;
        var added = recovered.Where(j => _items.All(i => i.Id != j.Id)).Select(j => new QueueItem(j, QueueItemPhase.Interrupted)).ToList();
        if (added.Count > 0) SetItems(_items.Concat(added).OrderBy(i => i.Job.RecordedAt));
        return recovered;
    }

    private RecordingRecovery.Report RunRecovery(string? legacyRecordings)
    {
        var report = RecordingRecovery.Run(_queueFolder, legacyRecordings, _settings.LanguageForEngine(_settings.PrimaryLanguage));
        if (report.Recovered.Count > 0 || report.DiscardedEmpty > 0 || report.Unrepairable.Count > 0)
            Log.Write($"recovery: {report.Recovered.Count} interrupted recording(s) rescued, {report.DiscardedEmpty} empty draft(s) removed, " +
                      $"{report.SkippedActive} active, unrepairable: [{string.Join(", ", report.Unrepairable)}]");
        return report;
    }

    private string FolderFor(Guid id) => MeetingJob.Folder(id, _queueFolder);

    private void TryWrite(MeetingJob job)
    {
        try { job.Write(_queueFolder); }
        catch (Exception ex) { Log.Write("meeting job not written: " + ex.Message); }
    }

    private static void DeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { Log.Write("queue folder not deleted: " + ex.Message); }
    }

    private void Replace(Guid id, QueueItem item) => SetItems(_items.Select(i => i.Id == id ? item : i));

    private void SetItems(IEnumerable<QueueItem> items)
    {
        _items = items.ToList();
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(InterruptedCount));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
