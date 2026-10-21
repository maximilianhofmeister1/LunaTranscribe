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

public enum QueueItemPhase { Waiting, Processing, Failed }

/// <summary>Ein Auftrag in der Warteschlange mit seinem Zustand (unveränderlich; die Warteschlange ersetzt ihn).</summary>
public sealed record QueueItem(MeetingJob Job, QueueItemPhase Phase, string Step = "", double Fraction = 0, string? Failure = null)
{
    public Guid Id => Job.Id;
    public bool IsProcessing => Phase == QueueItemPhase.Processing;
    public bool IsFailed => Phase == QueueItemPhase.Failed;
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
    public MeetingProcessingQueue(SettingsStore settings, ModelManager models, MeetingStore store, IMeetingPipeline pipeline,
                                  string? queueFolder = null)
    {
        _settings = settings;
        _models = models;
        _store = store;
        _pipeline = pipeline;
        _queueFolder = queueFolder ?? AppPaths.MeetingQueue;
        EngineProvider = ct => _models.WaitForReadyAsync(_settings.Engine, ct);
        Restore();
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
    public int PendingCount => _items.Count(i => !i.IsFailed);

    // MARK: Aufträge

    /// <summary>
    /// Übernimmt eine beendete Aufnahme: Datei nach <c>Queue\&lt;id&gt;\audio.wav</c> verschieben, Auftrag sichern,
    /// loslegen. Wirft, wenn das nicht geht – die Aufnahme bleibt dann, wo sie war.
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
            Write(job);
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

    /// <summary>Fehlgeschlagenen Auftrag erneut einreihen.</summary>
    public void Retry(Guid id)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        var job = item.Job with { Failure = null };
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

    /// <summary>Aufträge vom letzten Lauf wieder aufnehmen. Ein beim Beenden laufender Auftrag wartet einfach erneut.</summary>
    private void Restore()
    {
        var jobs = MeetingJob.ReadAll(_queueFolder);
        _items = jobs.Select(j => j.Failure is { } f
            ? new QueueItem(j, QueueItemPhase.Failed, Failure: f)
            : new QueueItem(j, QueueItemPhase.Waiting)).ToList();
        if (_items.Count > 0) Log.Write($"restored {_items.Count} queued meeting(s)");
    }

    private string FolderFor(Guid id) => Path.Combine(_queueFolder, id.ToString("D").ToUpperInvariant());

    private void Write(MeetingJob job) =>
        AtomicFile.WriteAllBytes(Path.Combine(FolderFor(job.Id), MeetingJob.JobFileName), JsonCoding.EncodeToUtf8(job));

    private void TryWrite(MeetingJob job)
    {
        try { Write(job); }
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
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
