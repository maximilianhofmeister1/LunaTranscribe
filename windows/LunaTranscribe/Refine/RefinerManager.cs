using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;

namespace LunaTranscribe.Refine;

/// <summary>Zustand des Nachbearbeitungs-Modells – dieselben Stufen wie bei der ASR (<see cref="ModelStateKind"/>).</summary>
public sealed record RefinerState(ModelStateKind Kind, double Progress = 0, RefinerKind? Refiner = null, string? Error = null)
{
    public static readonly RefinerState Idle = new(ModelStateKind.Idle);
}

/// <summary>
/// Hält höchstens ein geladenes Nachbearbeitungs-Modell, wechselt es bei Bedarf und entlädt es nach Inaktivität.
/// Gegenstück zu <see cref="ModelManager"/> – bewusst dieselbe Form, damit Menü, Overlay und Einstellungen beide
/// Modell-Familien gleich behandeln. Läuft auf dem UI-Thread.
/// </summary>
public sealed class RefinerManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>So lange wartet <see cref="RefineAsync"/> höchstens auf ein Modell, das gerade von der Platte lädt.</summary>
    public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(15);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _idleTimer;
    private RefinerState _state = RefinerState.Idle;
    private ITextRefiner? _refiner;
    private CancellationTokenSource? _loadCts;
    private TaskCompletionSource<bool>? _loadDone;
    private RefineWarmup? _warmup;
    private DateTime _lastUse = DateTime.Now;
    private TimeSpan _unloadAfter = TimeSpan.Zero;
    private int _activeRefines;

    /// <summary>Wird vor dem Entladen gefragt; false = gerade beschäftigt (Aufnahme mit Nachbearbeitung), später erneut versuchen.</summary>
    public Func<bool>? CanUnload { get; set; }

    public RefinerManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _idleTimer.Tick += (_, _) => CheckIdle();
    }

    /// <summary>Zeit ohne Nachbearbeitung, nach der das Modell entladen wird. Zero = nie.</summary>
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

    public RefinerState State
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
        ModelStateKind.Idle => "Kein Sprachmodell geladen",
        ModelStateKind.Unloaded => "Bereit – Sprachmodell wird bei Bedarf geladen",
        ModelStateKind.Loading when State.Progress < 0.9 && !FilesPresent => $"Sprachmodell wird geladen… {(int)(State.Progress / 0.9 * 100)} %",
        ModelStateKind.Loading when State.Progress < 0.95 => "Sprachmodell wird initialisiert…",
        ModelStateKind.Loading => "Sprachmodell wird aufgewärmt…",
        ModelStateKind.Ready => $"Bereit – {State.Refiner!.Value.ShortLabel()}",
        ModelStateKind.Failed => $"Fehler: {State.Error}",
        _ => "",
    };

    public ITextRefiner? Current => _refiner is { IsLoaded: true } ? _refiner : null;

    public bool IsReady => State.Kind == ModelStateKind.Ready;

    /// <summary>Modelldatei liegt lokal – Laden dauert nur Sekunden.</summary>
    public bool FilesPresent => _refiner?.FilesPresent ?? false;

    /// <summary>Merkt sich die letzte Nutzung (für den Inaktivitäts-Timer).</summary>
    public void Touch() => _lastUse = DateTime.Now;

    /// <summary>
    /// Lädt das gewünschte Modell (falls nicht bereits geladen). Entlädt vorher ein anderes.
    /// <paramref name="warmup"/>: Hauptsprache + eigene Anweisung – deren Prompt-Anfang wird beim Laden vorberechnet,
    /// damit schon die erste Nachbearbeitung schnell ist.
    /// </summary>
    public void EnsureLoaded(RefinerKind kind, RefineWarmup? warmup = null)
    {
        Touch();
        var warmupChanged = warmup != null && warmup != _warmup;
        if (warmup != null) _warmup = warmup;
        if (_refiner != null && _refiner.Kind == kind && (_refiner.IsLoaded || State.Kind == ModelStateKind.Loading))
        {
            // Sprache/Anweisung geändert: neuen Prompt-Anfang im Hintergrund vorberechnen (während des Ladens macht das PrepareAsync).
            if (warmupChanged && _refiner.IsLoaded)
            {
                var target = _refiner;
                _ = Task.Run(() => target.PrimeAsync(warmup!, CancellationToken.None));
            }
            return;
        }
        _loadCts?.Cancel();
        _loadDone?.TrySetResult(false);

        if (_refiner == null || _refiner.Kind != kind)
        {
            _refiner?.Dispose();
            _refiner = new LlamaRefiner(kind);
        }
        var refiner = _refiner;
        State = new RefinerState(ModelStateKind.Loading, 0, kind);

        var cts = new CancellationTokenSource();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadCts = cts;
        _loadDone = done;
        var sw = Stopwatch.StartNew();
        var hadFiles = refiner.FilesPresent;
        var warm = _warmup;

        _ = Task.Run(async () =>
        {
            try
            {
                await refiner.PrepareAsync(
                    fraction => _dispatcher.BeginInvoke(() => UpdateProgress(fraction, refiner, kind)),
                    warm, cts.Token);
                if (cts.IsCancellationRequested) return;
                Log.Write($"refiner {kind.Key()} loaded in {sw.Elapsed.TotalSeconds:0.0}s{(hadFiles ? "" : " (incl. download)")}");
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_refiner == refiner) { State = new RefinerState(ModelStateKind.Ready, 1, kind); Touch(); }
                });
                done.TrySetResult(true);
            }
            catch (OperationCanceledException) { done.TrySetResult(false); }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) { done.TrySetResult(false); return; }
                Log.Write("refiner load failed: " + ex);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_refiner == refiner) State = new RefinerState(ModelStateKind.Failed, 0, kind, Describe(ex));
                });
                done.TrySetResult(false);
            }
        });
    }

    /// <summary>
    /// Bereinigt ein Diktat. Wirft nie (außer bei Programmierfehlern im Aufrufer): bei jedem Problem – Modell lädt noch,
    /// Fehler, Timeout, unplausible Antwort, Abbruch – enthält das Ergebnis den Rohtext und (außer bei Abbruch) einen
    /// deutschen Hinweis. Ist das Modell entladen, die Datei aber vorhanden, wird es nachgeladen (höchstens <see cref="LoadWait"/>).
    /// </summary>
    /// <param name="promptTemplate">Eigene Anweisung aus den Einstellungen (mit <c>{language}</c>); null/leer = Standard.</param>
    public async Task<RefineResult> RefineAsync(string rawText, AppLanguage language, RefinerKind kind, string? promptTemplate,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(rawText)) return RefineResult.Fallback(rawText, RefineOutcome.Skipped, null, sw.Elapsed);

        Touch();
        _activeRefines++;
        try
        {
            var refiner = await ReadyRefinerAsync(kind, ct);
            if (refiner == null)
            {
                if (ct.IsCancellationRequested) return RefineResult.Fallback(rawText, RefineOutcome.Cancelled, null, sw.Elapsed);
                return State.Kind == ModelStateKind.Failed && State.Refiner == kind
                    ? RefineResult.Fallback(rawText, RefineOutcome.Failed, $"{State.Error} – Rohtext eingefügt.", sw.Elapsed)
                    : RefineResult.Fallback(rawText, RefineOutcome.NotReady, "Sprachmodell lädt noch – Rohtext eingefügt.", sw.Elapsed);
            }

            var refined = await refiner.RefineAsync(rawText, language, promptTemplate, ct);
            Log.Write($"refined {rawText.Length} → {refined.Length} chars in {sw.ElapsedMilliseconds}ms");
            return RefineResult.Success(refined, rawText, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RefineResult.Fallback(rawText, RefineOutcome.Cancelled, null, sw.Elapsed);
        }
        catch (RefinerException ex)
        {
            Log.Write($"refine failed ({ex.Outcome}): {ex.Message}");
            return RefineResult.Fallback(rawText, ex.Outcome, Notice(ex.Outcome), sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log.Write("refine failed: " + ex);
            return RefineResult.Fallback(rawText, RefineOutcome.Failed, Notice(RefineOutcome.Failed), sw.Elapsed);
        }
        finally
        {
            _activeRefines--;
            Touch();
        }
    }

    private static string Notice(RefineOutcome outcome) => outcome switch
    {
        RefineOutcome.NotReady => "Sprachmodell lädt noch – Rohtext eingefügt.",
        RefineOutcome.Timeout => "Nachbearbeitung hat zu lange gedauert – Rohtext eingefügt.",
        RefineOutcome.Implausible => "Keine brauchbare Antwort vom Sprachmodell – Rohtext eingefügt.",
        _ => "Nachbearbeitung fehlgeschlagen – Rohtext eingefügt.",
    };

    /// <summary>Geladenes Modell der gewünschten Art; lädt bei Bedarf nach, wenn die Datei schon lokal liegt.</summary>
    private async Task<ITextRefiner?> ReadyRefinerAsync(RefinerKind kind, CancellationToken ct)
    {
        if (Current is { } ready && ready.Kind == kind) return ready;
        if (State.Kind == ModelStateKind.Failed && State.Refiner == kind) return null;   // Retry macht der Nutzer

        EnsureLoaded(kind);
        if (!FilesPresent) return null;       // Download läuft – nicht minutenlang warten

        var done = _loadDone;
        if (done != null)
        {
            try { await done.Task.WaitAsync(LoadWait, ct); }
            catch (TimeoutException) { return null; }
            catch (OperationCanceledException) { return null; }
        }
        return Current is { } loaded && loaded.Kind == kind ? loaded : null;
    }

    private void CheckIdle()
    {
        if (_unloadAfter <= TimeSpan.Zero || State.Kind != ModelStateKind.Ready) return;
        if (DateTime.Now - _lastUse < _unloadAfter) return;
        if (_activeRefines > 0 || (CanUnload != null && !CanUnload())) return;
        Unload(ModelStateKind.Unloaded);
    }

    /// <summary>Gibt das Modell frei; die Refiner-Instanz bleibt (kennt Datei/Art) und lädt bei Bedarf neu.</summary>
    public void Unload(ModelStateKind newState = ModelStateKind.Unloaded)
    {
        _loadCts?.Cancel();
        _loadDone?.TrySetResult(false);
        if (_refiner == null) return;
        var kind = _refiner.Kind;
        _refiner.Unload();
        Log.Write($"refiner {kind.Key()} unloaded (idle)");
        State = new RefinerState(newState, 0, kind);
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.Message;
        if (ex is System.Net.Http.HttpRequestException) return "Download fehlgeschlagen – keine Internetverbindung? (" + msg + ")";
        if (ex is LLama.Exceptions.LoadWeightsFailedException) return "Sprachmodell konnte nicht geladen werden (Datei beschädigt?).";
        if (msg.Length > 200) msg = msg[..200] + "…";
        return msg;
    }

    private void UpdateProgress(double fraction, ITextRefiner target, RefinerKind kind)
    {
        if (_refiner != target || State.Kind != ModelStateKind.Loading) return;
        State = new RefinerState(ModelStateKind.Loading, fraction, kind);
    }

    public void Retry(RefinerKind kind)
    {
        _refiner?.Dispose();
        _refiner = null;
        EnsureLoaded(kind);
    }

    public void Shutdown()
    {
        _idleTimer.Stop();
        _loadCts?.Cancel();
        _refiner?.Dispose();
        _refiner = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
