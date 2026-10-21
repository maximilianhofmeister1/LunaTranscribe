using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Search;

/// <summary>
/// Hält den Suchindex für luna-mcp aktuell, solange Claude Meetings lesen darf: Stichwort-Teil nach jeder Änderung der
/// Bibliothek, Vektoren für die inhaltliche Suche im Hintergrund.
///
/// Anders als auf dem Mac rechnet die App nicht selbst: Sie startet <c>mcp\luna-mcp.exe --index</c> als Kindprozess.
/// So bleibt die ONNX Runtime von luna-mcp aus dem App-Prozess heraus (sherpa-onnx bringt eine eigene, andere Version
/// mit), und das Modell belegt nur Speicher, solange indexiert wird. <see cref="SearchIndex.Sync"/> ist idempotent;
/// SQLite (WAL) regelt den gleichzeitigen Zugriff mit einem laufenden Claude.
///
/// Rücksicht: Nie während ein Meeting verarbeitet wird (<see cref="IsBusy"/>) – ein laufender Durchgang wird dann
/// abgebrochen (SQLite-Transaktionen machen das sicher) und danach nachgeholt.
/// Läuft auf dem UI-Thread; Prozess-Ausgaben kommen über den Dispatcher zurück.
/// </summary>
public sealed class SearchIndexer : INotifyPropertyChanged, IDisposable
{
    public enum ModelStateKind { Off, NotDownloaded, Downloading, Ready, Failed }

    public sealed record ModelState(ModelStateKind Kind, double Progress = 0, string? Error = null);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Wird gerade ein Meeting verarbeitet (Transkription, Sprechererkennung)? Dann wird nicht indexiert.
    /// Setzt die Meeting-Verarbeitung; ohne sie gilt „nie beschäftigt“.
    /// </summary>
    public Func<bool> IsBusy { get; set; } = () => false;

    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly EmbeddingModelKind _kind = EmbeddingModelKinds.Standard;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _busyCheck;
    private FileSystemWatcher? _watcher;
    private Process? _indexProcess;
    private Process? _downloadProcess;
    private bool _runPending;
    private bool _cancelledForBusy;
    private bool _stopped;
    private ModelState _model = new(ModelStateKind.Off);
    private IndexStats? _stats;
    private bool _isIndexing;
    private string? _lastError;

    public SearchIndexer(SettingsStore settings, Dispatcher dispatcher, bool interactive = true)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        // Verzögert: Tippen im Titel oder mehrere Dateien eines Meetings sollen nur einen Durchgang auslösen.
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ScheduleRun();
        };
        _busyCheck = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        _busyCheck.Tick += (_, _) => CheckBusy();
        UpdateModelState();
        RefreshStats();

        if (!interactive) return;
        _settings.PropertyChanged += OnSettingsChanged;
        Watch();
        _busyCheck.Start();
        // Beim Start nicht mit dem Laden des Sprachmodells konkurrieren.
        var initial = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        initial.Tick += (_, _) =>
        {
            initial.Stop();
            ScheduleRun();
        };
        initial.Start();
    }

    // MARK: Zustand

    public ModelState Model
    {
        get => _model;
        private set { if (_model == value) return; _model = value; OnPropertyChanged(); }
    }

    /// <summary>Stand des Index (null = noch nicht gelesen oder gelöscht).</summary>
    public IndexStats? Stats
    {
        get => _stats;
        private set { if (_stats == value) return; _stats = value; OnPropertyChanged(); }
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set { if (_isIndexing == value) return; _isIndexing = value; OnPropertyChanged(); }
    }

    /// <summary>Fehler des letzten Durchgangs, sonst null.</summary>
    public string? LastError
    {
        get => _lastError;
        private set { if (_lastError == value) return; _lastError = value; OnPropertyChanged(); }
    }

    // MARK: Steuerung

    /// <summary>Durchgang anstoßen (z. B. nach einer Änderung der Bibliothek); läuft einer, folgt danach ein weiterer.</summary>
    public void ScheduleRun()
    {
        if (!_settings.McpAccessEnabled) return;
        if (_indexProcess is not null || IsBusy())
        {
            _runPending = true;
            return;
        }
        StartIndexing();
    }

    /// <summary>Bibliothek hat sich geändert – verzögert neu indexieren.</summary>
    public void LibraryChanged()
    {
        if (!_settings.McpAccessEnabled) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Lädt das Embedding-Modell (einmalig, ~470 MB) über luna-mcp und indexiert danach.</summary>
    public void DownloadModel()
    {
        if (_downloadProcess is not null) return;
        if (!ClaudeConnection.HelperExists)
        {
            Model = new ModelState(ModelStateKind.Failed, Error: "luna-mcp wurde nicht gefunden.");
            return;
        }
        Model = new ModelState(ModelStateKind.Downloading, 0);
        string? failure = null;
        string? done = null;
        try
        {
            var process = Process.Start(ClaudeConnection.HelperStartInfo("--download-embedding-model"))!;
            _downloadProcess = process;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (line.StartsWith("progress ", StringComparison.Ordinal)
                    && double.TryParse(line[9..], NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
                    _dispatcher.BeginInvoke(() => { if (Model.Kind == ModelStateKind.Downloading) Model = new ModelState(ModelStateKind.Downloading, fraction); });
                else if (line.StartsWith("error ", StringComparison.Ordinal)) failure = line[6..];
                else if (line.StartsWith("done", StringComparison.Ordinal)) done = line;
            };
            process.ErrorDataReceived += (_, _) => { };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                var code = process.ExitCode;
                _dispatcher.BeginInvoke(() =>
                {
                    _downloadProcess = null;
                    process.Dispose();
                    if (code == 0 && _kind.IsDownloaded())
                    {
                        Log.Write("search: embedding model ready " + done);
                        Model = new ModelState(ModelStateKind.Ready);
                        ScheduleRun();
                    }
                    else
                    {
                        Log.Write("search: embedding model failed: " + failure);
                        Model = new ModelState(ModelStateKind.Failed, Error: failure ?? $"luna-mcp beendet mit Code {code}.");
                    }
                });
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _downloadProcess = null;
            Model = new ModelState(ModelStateKind.Failed, Error: ex.Message);
        }
    }

    /// <summary>Löscht den Suchindex; er wird beim nächsten Durchgang (oder von luna-mcp) neu aufgebaut.</summary>
    public void DeleteIndex()
    {
        StopIndexing();
        try
        {
            SearchIndex.Delete(AppPaths.SearchIndex);
            Log.Write("search: index deleted");
            LastError = null;
        }
        catch (Exception ex)
        {
            Log.Write("search: index delete failed: " + ex.Message);
            LastError = "Suchindex konnte nicht gelöscht werden: " + ex.Message;
        }
        Stats = null;
        // Neu aufbauen (luna-mcp holt es sonst beim nächsten Zugriff nach).
        LibraryChanged();
    }

    /// <summary>Liest den Stand des Index (im Hintergrund, direkt über LunaCore – ohne Modell).</summary>
    public void RefreshStats()
    {
        var model = _settings.SemanticSearchEnabled ? _kind.ModelID() : null;
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(AppPaths.SearchIndex)) return (IndexStats?)null;
                using var index = new SearchIndex(AppPaths.SearchIndex);
                return index.Stats(model);
            }
            catch (Exception ex)
            {
                Log.Write("search: stats failed: " + ex.Message);
                return null;
            }
        }).ContinueWith(t => _dispatcher.BeginInvoke(() => Stats = t.Result), TaskScheduler.Default);
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _debounce.Stop();
        _busyCheck.Stop();
        _watcher?.Dispose();
        StopIndexing();
        try { _downloadProcess?.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    // MARK: Ablauf

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsStore.McpAccessEnabled):
            case nameof(SettingsStore.SemanticSearchEnabled):
                ConfigurationChanged();
                break;
            case nameof(SettingsStore.MeetingFolder):
                Watch();
                LibraryChanged();
                break;
        }
    }

    private void ConfigurationChanged()
    {
        UpdateModelState();
        if (!_settings.McpAccessEnabled)
        {
            StopIndexing();
            return;
        }
        if (_settings.SemanticSearchEnabled && Model.Kind == ModelStateKind.NotDownloaded)
        {
            DownloadModel();
            return;
        }
        ScheduleRun();
    }

    private void UpdateModelState()
    {
        if (Model.Kind == ModelStateKind.Downloading) return;
        if (!_settings.SemanticSearchEnabled) Model = new ModelState(ModelStateKind.Off);
        else Model = new ModelState(_kind.IsDownloaded() ? ModelStateKind.Ready : ModelStateKind.NotDownloaded);
    }

    /// <summary>Beobachtet den Meeting-Ordner und den Rettungsordner auf geänderte <c>meeting.json</c> und umbenannte Ordner.</summary>
    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        var folder = AppPaths.MeetingFolder(_settings.MeetingFolder);
        if (!Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, e) =>
            {
                if (e.ChangeType == WatcherChangeTypes.Changed && !e.FullPath.EndsWith(MeetingLibrary.TranscriptFileName, StringComparison.OrdinalIgnoreCase)) return;
                _dispatcher.BeginInvoke(LibraryChanged);
            };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => _dispatcher.BeginInvoke(LibraryChanged);
            watcher.Error += (_, _) => _dispatcher.BeginInvoke(LibraryChanged);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Write("search: watching meeting folder failed: " + ex.Message);
        }
    }

    private void StartIndexing()
    {
        if (!ClaudeConnection.HelperExists)
        {
            LastError = "luna-mcp wurde nicht gefunden – der Suchindex wird erst beim nächsten Zugriff durch Claude aufgebaut.";
            return;
        }
        _runPending = false;
        _cancelledForBusy = false;
        _stopped = false;
        IsIndexing = true;
        string? failure = null;
        try
        {
            var process = Process.Start(ClaudeConnection.HelperStartInfo("--index"))!;
            _indexProcess = process;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (line.StartsWith("stats ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    if (parts.Length == 4 && int.TryParse(parts[1], out var meetings) && int.TryParse(parts[2], out var chunks)
                        && int.TryParse(parts[3], out var embedded))
                        _dispatcher.BeginInvoke(() => Stats = new IndexStats(meetings, chunks, embedded));
                }
                else if (line.StartsWith("error ", StringComparison.Ordinal))
                {
                    failure = line[6..];
                }
                else if (line.StartsWith("sync ", StringComparison.Ordinal) && !line.StartsWith("sync +0 ~0 -0 ", StringComparison.Ordinal))
                {
                    Log.Write("search: " + line);
                }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                int code;
                try { code = process.ExitCode; } catch (Exception) { code = -1; }
                _dispatcher.BeginInvoke(() => FinishIndexing(process, code, failure));
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _indexProcess = null;
            IsIndexing = false;
            LastError = "Indexieren fehlgeschlagen: " + ex.Message;
        }
    }

    private void FinishIndexing(Process process, int code, string? failure)
    {
        if (_indexProcess == process) _indexProcess = null;
        process.Dispose();
        IsIndexing = _indexProcess is not null;
        if (_cancelledForBusy)
        {
            _runPending = true;
        }
        else if (_stopped)
        {
            // Absichtlich beendet (Freigabe aus, Index gelöscht) – kein Fehler.
        }
        else if (code != 0)
        {
            LastError = "Indexieren fehlgeschlagen: " + (failure ?? $"luna-mcp beendet mit Code {code}.");
            Log.Write("search: " + LastError);
        }
        else
        {
            LastError = null;
        }
        if (_runPending && !IsBusy()) ScheduleRun();
    }

    /// <summary>Alle paar Sekunden: laufenden Durchgang abbrechen, sobald ein Meeting verarbeitet wird; danach nachholen.</summary>
    private void CheckBusy()
    {
        bool busy;
        try { busy = IsBusy(); }
        catch (Exception) { busy = false; }
        if (busy && _indexProcess is not null)
        {
            Log.Write("search: indexing paused while a meeting is processed");
            _cancelledForBusy = true;
            StopIndexing();
        }
        else if (!busy && _runPending && _indexProcess is null)
        {
            ScheduleRun();
        }
    }

    private void StopIndexing()
    {
        if (_indexProcess is not { } process) return;
        _stopped = true;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
