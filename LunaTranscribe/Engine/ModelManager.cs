using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LunaTranscribe.Output;

namespace LunaTranscribe.Engine;

public enum ModelStateKind
{
    Idle,       // noch nie geladen
    Unloaded,   // nach Inaktivität entladen – wird beim nächsten Diktat wieder geladen
    Loading,
    Ready,
    Failed,
}

public sealed record ModelState(ModelStateKind Kind, double Progress = 0, EngineKind? Engine = null, string? Error = null)
{
    public static readonly ModelState Idle = new(ModelStateKind.Idle);
}

/// <summary>
/// Hält höchstens eine geladene Engine im Speicher, wechselt sie bei Bedarf und entlädt sie nach
/// Inaktivität wieder (spart ~700 MB RAM). Läuft auf dem UI-Thread.
/// </summary>
public sealed class ModelManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _idleTimer;
    private ModelState _state = ModelState.Idle;
    private ITranscriptionEngine? _engine;
    private CancellationTokenSource? _loadCts;
    private TaskCompletionSource<bool>? _loadDone;
    private DateTime _lastUse = DateTime.Now;
    private TimeSpan _unloadAfter = TimeSpan.Zero;

    /// <summary>Wird vor dem Entladen gefragt; false = gerade beschäftigt (Aufnahme/Transkription), später erneut versuchen.</summary>
    public Func<bool>? CanUnload { get; set; }

    public ModelManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => CheckIdle();
    }

    /// <summary>Zeit ohne Diktat, nach der das Modell entladen wird. Zero = nie.</summary>
    public TimeSpan UnloadAfter
    {
        get => _unloadAfter;
        set
        {
            _unloadAfter = value;
            if (value > TimeSpan.Zero) { _lastUse = DateTime.Now; _idleTimer.Start(); }
            else _idleTimer.Stop();
        }
    }

    public ModelState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsReady));
        }
    }

    /// <summary>Einheitlicher Statustext für Menü, Overlay und Einstellungen.</summary>
    public string StatusText => State.Kind switch
    {
        ModelStateKind.Idle => "Kein Modell geladen",
        ModelStateKind.Unloaded => "Bereit – Modell wird beim nächsten Diktat geladen",
        ModelStateKind.Loading when State.Progress < 0.9 && !FilesPresent => $"Modell wird geladen… {(int)(State.Progress / 0.9 * 100)} %",
        ModelStateKind.Loading when State.Progress < 0.95 => "Modell wird initialisiert…",
        ModelStateKind.Loading => "Modell wird aufgewärmt…",
        ModelStateKind.Ready => $"Bereit – {State.Engine!.Value.ShortLabel()}",
        ModelStateKind.Failed => $"Fehler: {State.Error}",
        _ => "",
    };

    public ITranscriptionEngine? Current => _engine is { IsLoaded: true } ? _engine : null;

    public bool IsReady => State.Kind == ModelStateKind.Ready;

    /// <summary>Modelldateien liegen lokal – Laden dauert nur Sekunden, eine Aufnahme kann sofort starten.</summary>
    public bool FilesPresent => _engine?.FilesPresent ?? false;

    /// <summary>Aufnahme darf starten: Modell bereit oder in Sekunden nachladbar.</summary>
    public bool CanRecord => IsReady || FilesPresent;

    /// <summary>Merkt sich die letzte Nutzung (für den Inaktivitäts-Timer).</summary>
    public void Touch() => _lastUse = DateTime.Now;

    /// <summary>Lädt die gewünschte Engine (falls nicht bereits geladen). Entlädt vorher eine andere.</summary>
    public void EnsureLoaded(EngineKind kind)
    {
        Touch();
        if (_engine != null && _engine.Kind == kind && (_engine.IsLoaded || State.Kind == ModelStateKind.Loading)) return;
        _loadCts?.Cancel();
        _loadDone?.TrySetResult(false);

        if (_engine == null || _engine.Kind != kind)
        {
            _engine?.Dispose();
            _engine = kind == EngineKind.Cohere ? new CohereEngine() : new ParakeetEngine();
        }
        var engine = _engine;
        State = new ModelState(ModelStateKind.Loading, 0, kind);

        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadCts = cts;
        _loadDone = done;
        var sw = Stopwatch.StartNew();
        var hadFiles = engine.FilesPresent;

        _ = Task.Run(async () =>
        {
            try
            {
                await engine.PrepareAsync(
                    fraction => _dispatcher.BeginInvoke(() => UpdateProgress(fraction, engine, kind)),
                    cts.Token);
                if (cts.IsCancellationRequested) return;
                Log.Write($"model {kind.Key()} loaded in {sw.Elapsed.TotalSeconds:0.0}s{(hadFiles ? "" : " (incl. download)")}");
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_engine == engine) { State = new ModelState(ModelStateKind.Ready, 1, kind); Touch(); }
                });
                done.TrySetResult(true);
            }
            catch (OperationCanceledException) { done.TrySetResult(false); }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) { done.TrySetResult(false); return; }
                Log.Write("model load failed: " + ex);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_engine == engine) State = new ModelState(ModelStateKind.Failed, 0, kind, Describe(ex));
                });
                done.TrySetResult(false);
            }
        });
    }

    /// <summary>
    /// Wartet, bis die Engine bereit ist (lädt sie bei Bedarf). Wirft bei Fehler.
    /// Wird von der Transkription genutzt, wenn die Aufnahme parallel zum Laden lief.
    /// </summary>
    public async Task<ITranscriptionEngine> WaitForReadyAsync(EngineKind kind, CancellationToken ct)
    {
        if (Current is { } ready && ready.Kind == kind) return ready;
        if (State.Kind != ModelStateKind.Loading || _engine?.Kind != kind) EnsureLoaded(kind);

        var done = _loadDone;
        if (done != null) await done.Task.WaitAsync(ct);

        if (Current is { } engine && engine.Kind == kind) return engine;
        throw new EngineException(State.Kind == ModelStateKind.Failed
            ? "Modell konnte nicht geladen werden: " + State.Error
            : "Das Modell ist nicht geladen.");
    }

    private void CheckIdle()
    {
        if (_unloadAfter <= TimeSpan.Zero || State.Kind != ModelStateKind.Ready) return;
        if (DateTime.Now - _lastUse < _unloadAfter) return;
        if (CanUnload != null && !CanUnload()) return;
        Unload(ModelStateKind.Unloaded);
    }

    /// <summary>Gibt das Modell frei; die Engine-Instanz bleibt (kennt Dateien/Konfiguration) und lädt bei Bedarf neu.</summary>
    public void Unload(ModelStateKind newState = ModelStateKind.Unloaded)
    {
        _loadCts?.Cancel();
        _loadDone?.TrySetResult(false);
        if (_engine == null) return;
        var kind = _engine.Kind;
        _engine.Unload();
        // Native Puffer der ONNX Runtime sind mit dem Recognizer weg; den Rest räumt der GC.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Log.Write($"model {kind.Key()} unloaded (idle)");
        State = new ModelState(newState, 0, kind);
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (ex is System.Net.Http.HttpRequestException) return "Download fehlgeschlagen – keine Internetverbindung? (" + msg + ")";
        if (msg.Length > 200) msg = msg[..200] + "…";
        return msg;
    }

    private void UpdateProgress(double fraction, ITranscriptionEngine target, EngineKind kind)
    {
        if (_engine != target || State.Kind != ModelStateKind.Loading) return;
        State = new ModelState(ModelStateKind.Loading, fraction, kind);
    }

    public void Retry(EngineKind kind)
    {
        _engine?.Dispose();
        _engine = null;
        EnsureLoaded(kind);
    }

    public void Shutdown()
    {
        _idleTimer.Stop();
        _loadCts?.Cancel();
        _engine?.Dispose();
        _engine = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
