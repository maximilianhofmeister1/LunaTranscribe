using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using LunaTranscribe.Audio;
using LunaTranscribe.Engine;
using LunaTranscribe.Hotkeys;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;
using LunaTranscribe.UI.MainWindow;
using LunaCore;

namespace LunaTranscribe.Core;

public sealed record Transcript(DateTime Date, string Text, AppLanguage Language, double DurationSeconds, double ProcessingSeconds)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Rohtext vor der Nachbearbeitung; null = nicht nachbearbeitet.</summary>
    public string? RawText { get; init; }
    public string ProcessingLabel => $"{ProcessingSeconds:0.0}s";
}

public enum StatusKind { Idle, Recording, Transcribing, Refining, Notice }

/// <summary>Zentraler Zustand der App. Verbindet Hotkeys, Aufnahme, Engine und Texteinfügen. Läuft auf dem UI-Thread.</summary>
public sealed class AppState : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsStore Settings { get; } = SettingsStore.Shared;
    public ModelManager Models { get; }
    public ObservableCollection<Transcript> History { get; } = new();
    public MainRouter Router { get; } = new();

    /// <summary>Zeigt das Hauptfenster (setzt App beim Start).</summary>
    public Action? ShowMainWindow { get; set; }

    private readonly Dispatcher _dispatcher;
    private readonly AudioRecorder _recorder = new();
    private readonly MediaPlaybackController _media = new();
    private readonly KeyboardHook _hook;
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _permissionTimer;
    private DispatcherTimer? _noticeTimer;

    private StatusKind _status = StatusKind.Idle;
    private string _noticeText = "";
    private float _inputLevel;
    private bool _microphoneAllowed = Permissions.MicrophoneAllowed;
    private AppLanguage _activeLanguage = AppLanguage.German;
    private DateTime _recordingStart;
    /// <summary>Zählt Transkriptionen; ein Abbruch erhöht den Wert, sodass ein verspätetes Ergebnis verworfen wird.</summary>
    private int _transcriptionGeneration;
    private bool _starting;

    /// <param name="interactive">false = Selbsttests: kein Tastatur-Hook, keine vorgeladenen Modelle, keine Hintergrund-Timer.</param>
    public AppState(Dispatcher dispatcher, bool interactive = true)
    {
        Interactive = interactive;
        _dispatcher = dispatcher;
        Models = new ModelManager(dispatcher);
        Models.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ModelManager.State) or nameof(ModelManager.StatusText) or nameof(ModelManager.IsReady))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanStartRecording));
            }
        };

        _hook = new KeyboardHook(dispatcher) { Primary = Settings.PrimaryHotkey, Secondary = Settings.SecondaryHotkey };
        _hook.HotkeyDown += HotkeyDown;
        _hook.HotkeyUp += HotkeyUp;
        _hook.EscapeDown += () => { if (Status != StatusKind.Idle) Cancel(); };
        if (interactive) _hook.Install();

        Settings.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsStore.PrimaryHotkey): _hook.Primary = Settings.PrimaryHotkey; break;
                case nameof(SettingsStore.SecondaryHotkey): _hook.Secondary = Settings.SecondaryHotkey; break;
                case nameof(SettingsStore.Engine): Models.EnsureLoaded(Settings.Engine); break;
                case nameof(SettingsStore.UnloadAfterMinutes): Models.UnloadAfter = TimeSpan.FromMinutes(Settings.UnloadAfterMinutes); break;
            }
        };

        // Modell direkt beim Start laden, damit der erste Hotkey sofort reagiert.
        Models.CanUnload = () => Status == StatusKind.Idle;
        Models.UnloadAfter = TimeSpan.FromMinutes(Settings.UnloadAfterMinutes);
        if (interactive) Models.EnsureLoaded(Settings.Engine);

        _levelTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += (_, _) => InputLevel = _recorder.Level;

        // Mikrofon-Datenschutz regelmäßig prüfen (ändert sich in den Windows-Einstellungen).
        _permissionTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _permissionTimer.Tick += (_, _) => MicrophoneAllowed = Permissions.MicrophoneAllowed;
        if (interactive) _permissionTimer.Start();

        // Medien-Steuerung (WinRT) im Hintergrund vorwärmen, damit der erste Hotkey nicht darauf wartet.
        if (interactive) _ = Task.Run(_media.WarmUpAsync);
    }

    /// <summary>false in Selbsttests (kein Hook, keine Modelle).</summary>
    public bool Interactive { get; }

    /// <summary>Wird von der UI gesetzt, solange der Hotkey-Recorder den Fokus hat.</summary>
    public bool HotkeysSuspended
    {
        get => _hook.Suspended;
        set => _hook.Suspended = value;
    }

    // MARK: Zustand

    public StatusKind Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsTranscribing));
            OnPropertyChanged(nameof(IsRefining));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanStartRecording));
        }
    }

    public string NoticeText
    {
        get => _noticeText;
        private set { _noticeText = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public float InputLevel
    {
        get => _inputLevel;
        private set { if (Math.Abs(_inputLevel - value) < 0.005f) return; _inputLevel = value; OnPropertyChanged(); }
    }

    public bool MicrophoneAllowed
    {
        get => _microphoneAllowed;
        private set { if (_microphoneAllowed == value) return; _microphoneAllowed = value; OnPropertyChanged(); }
    }

    private string _transcribingLabel = "Transkribiere…";

    /// <summary>Text im Overlay während der Verarbeitung („Modell wird geladen…“ oder „Transkribiere…“).</summary>
    public string TranscribingLabel
    {
        get => _transcribingLabel;
        private set { if (_transcribingLabel == value) return; _transcribingLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>Aufnahme kann starten: Modell bereit oder lokal vorhanden (lädt dann parallel zur Aufnahme).</summary>
    public bool CanStartRecording => Models.CanRecord && Status is not (StatusKind.Transcribing or StatusKind.Refining);

    public bool IsRecording => Status == StatusKind.Recording;
    public bool IsTranscribing => Status == StatusKind.Transcribing;
    public bool IsRefining => Status == StatusKind.Refining;
    public bool IsIdle => Status == StatusKind.Idle;
    public bool IsBusy => Status != StatusKind.Idle;

    /// <summary>Statuszeile für Menü und Tooltip.</summary>
    public string StatusText
    {
        get
        {
            if (!Models.IsReady && Status == StatusKind.Idle) return Models.StatusText;
            return Status switch
            {
                StatusKind.Recording => "Aufnahme läuft",
                StatusKind.Transcribing => TranscribingLabel,
                StatusKind.Refining => "Formuliere…",
                StatusKind.Notice => NoticeText,
                _ => Models.StatusText,
            };
        }
    }

    // MARK: Hauptfenster

    public void OpenMainWindow(MainPage? page = null)
    {
        if (page is { } p) Router.Page = p;
        ShowMainWindow?.Invoke();
    }

    public void OpenSettings(SettingsSection? section = null)
    {
        if (section is { } s) Router.SettingsSection = s;
        OpenMainWindow(MainPage.Settings);
    }

    public void OpenMeeting(Guid id)
    {
        Router.SelectedMeetingId = id;
        OpenMainWindow(MainPage.Meetings);
    }

    // MARK: Meeting & Wörterbuch (Platzhalter bis zur Integration)

    public int SuggestionCount => 0;
    public bool MeetingRecording => false;
    public string MeetingElapsedText => "";
    public int MeetingsPending => 0;
    public int MeetingCount => 0;
    public void ToggleMeetingFromMenu() { }

    /// <summary>Nur für UI-Snapshots: zeigt einen Zustand, ohne aufzunehmen.</summary>
    internal void PreviewStatus(StatusKind status, string notice = "")
    {
        NoticeText = notice;
        Status = status;
    }

    // MARK: Abbrechen (Escape / X im Overlay)

    public void Cancel()
    {
        switch (Status)
        {
            case StatusKind.Recording:
                _levelTimer.Stop();
                InputLevel = 0;
                _ = _recorder.Stop();   // Samples verwerfen
                _ = _media.ResumeIfPausedAsync();
                PlayStop();
                Status = StatusKind.Idle;
                break;
            case StatusKind.Transcribing:
            case StatusKind.Refining:
                _transcriptionGeneration++;   // laufendes Ergebnis wird ignoriert
                Status = StatusKind.Idle;
                break;
            case StatusKind.Notice:
                Status = StatusKind.Idle;
                break;
        }
    }

    // MARK: Hotkeys

    private AppLanguage LanguageFor(HotkeySlot slot)
    {
        var raw = slot == HotkeySlot.Primary ? Settings.PrimaryLanguage : Settings.SecondaryLanguage;
        return Settings.LanguageForEngine(raw);
    }

    private void HotkeyDown(HotkeySlot slot)
    {
        switch (Status)
        {
            case StatusKind.Recording:
                if (Settings.RecordingMode == RecordingMode.Toggle) StopRecordingAndTranscribe();
                break;
            case StatusKind.Idle:
            case StatusKind.Notice:
                StartRecording(LanguageFor(slot));
                break;
        }
    }

    private void HotkeyUp(HotkeySlot slot)
    {
        if (Settings.RecordingMode != RecordingMode.Hold) return;
        if (Status == StatusKind.Recording) StopRecordingAndTranscribe();
    }

    // MARK: Aufnahme

    /// <param name="refine">Diktat mit Nachbearbeitung (wie Hotkey 3).</param>
    public void ToggleRecordingFromMenu(bool refine = false)
    {
        if (Status == StatusKind.Recording) StopRecordingAndTranscribe();
        else StartRecording(LanguageFor(HotkeySlot.Primary));
    }

    private void StartRecording(AppLanguage language)
    {
        if (_starting) return;
        if (Models.State.Kind == ModelStateKind.Failed)
        {
            ShowNotice("Modell konnte nicht geladen werden: " + Models.State.Error);
            Models.Retry(Settings.Engine);
            return;
        }
        if (!Models.CanRecord)
        {
            // Erster Start: Download läuft noch – erst danach diktieren.
            ShowNotice(Models.StatusText);
            return;
        }
        // Modell (nach Inaktivität entladen) parallel zur Aufnahme nachladen – kostet keine Zeit beim Sprechen.
        Models.EnsureLoaded(Settings.Engine);

        var t0 = Stopwatch.StartNew();
        Log.Write("hotkey down");
        _starting = true;

        // Wiedergabe parallel pausieren – darf den Mikrofonstart nicht aufhalten
        // (der erste WinRT-Aufruf kann mehrere 100 ms dauern, deshalb nicht auf dem UI-Thread).
        if (Settings.PauseMedia)
        {
            var media = _media;
            _ = Task.Run(async () =>
            {
                await media.PauseIfPlayingAsync();
                Log.Write($"media paused +{t0.ElapsedMilliseconds}ms");
            });
        }

        try
        {
            _recorder.OnFirstBuffer = () => Log.Write($"first audio buffer +{t0.ElapsedMilliseconds}ms");
            _recorder.Start(Settings.InputDeviceUid);
            Log.Write($"recorder started +{t0.ElapsedMilliseconds}ms");
        }
        catch (AudioRecorderException ex)
        {
            _starting = false;
            _ = _media.ResumeIfPausedAsync();
            Log.Write("recorder start failed: " + ex.Message + (ex.InnerException != null ? " / " + ex.InnerException.Message : ""));
            ShowNotice(ex.Message);
            if (ex.Kind == AudioRecorderErrorKind.AccessDenied) Permissions.OpenMicrophoneSettings();
            return;
        }
        catch (Exception ex)
        {
            _starting = false;
            _ = _media.ResumeIfPausedAsync();
            Log.Write("recorder start failed: " + ex);
            ShowNotice("Aufnahme konnte nicht gestartet werden: " + ex.Message);
            return;
        }

        _starting = false;
        _activeLanguage = language;
        _recordingStart = DateTime.Now;
        Status = StatusKind.Recording;
        PlayStart();
        _levelTimer.Start();
    }

    private void StopRecordingAndTranscribe()
    {
        _levelTimer.Stop();
        InputLevel = 0;
        var samples = _recorder.Stop();
        var duration = (double)samples.Length / AudioRecorder.TargetSampleRate;
        _ = _media.ResumeIfPausedAsync();
        PlayStop();

        // Zu kurz (versehentlicher Tastendruck) → ignorieren
        if (duration < 0.3)
        {
            Status = StatusKind.Idle;
            return;
        }

        TranscribingLabel = Models.IsReady ? "Transkribiere…" : "Modell wird geladen…";
        Status = StatusKind.Transcribing;
        var language = _activeLanguage;
        var keepHistory = Settings.KeepHistory;
        var generation = ++_transcriptionGeneration;
        Log.Write($"transcribe start ({duration:0.0}s audio)");

        _ = TranscribeAsync(samples, language, duration, keepHistory, generation);
    }

    private async Task TranscribeAsync(float[] samples, AppLanguage language, double duration, bool keepHistory, int generation)
    {
        var t0 = Stopwatch.StartNew();
        try
        {
            var engine = await Models.WaitForReadyAsync(Settings.Engine, System.Threading.CancellationToken.None);
            if (generation != _transcriptionGeneration) return;
            if (t0.ElapsedMilliseconds > 50) Log.Write($"waited {t0.Elapsed.TotalSeconds:0.0}s for model");
            TranscribingLabel = "Transkribiere…";
            var text = await engine.TranscribeAsync(samples, language, System.Threading.CancellationToken.None);
            Models.Touch();
            samples = Array.Empty<float>();   // Aufnahme existiert danach nirgends mehr
            var elapsed = t0.Elapsed.TotalSeconds;
            Log.Write($"transcribe done in {elapsed:0.00}s, {text.Length} chars");

            // Wurde inzwischen abgebrochen (Escape/X)? Dann Ergebnis verwerfen.
            if (generation != _transcriptionGeneration) return;

            if (text.Length == 0)
            {
                ShowNotice("Nichts erkannt.");
                return;
            }

            Status = StatusKind.Idle;   // Overlay zuerst weg, damit es beim Einfügen nicht im Weg ist
            if (keepHistory)
            {
                History.Insert(0, new Transcript(DateTime.Now, text, language, duration, elapsed));
                while (History.Count > 20) History.RemoveAt(History.Count - 1);
            }
            Settings.DailyDictationStats = Settings.DailyDictationStats.Adding(DailyDictationStats.CountWords(text));
            await TextInjector.InsertAsync(text);
        }
        catch (Exception ex)
        {
            Log.Write("transcribe failed: " + ex);
            if (generation != _transcriptionGeneration) return;
            ShowNotice("Fehler: " + ex.Message);
        }
    }

    public void ClearHistory() => History.Clear();

    // MARK: Helfer

    private void ShowNotice(string text)
    {
        NoticeText = text;
        Status = StatusKind.Notice;
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer?.Stop();
            if (Status == StatusKind.Notice) Status = StatusKind.Idle;
        };
        _noticeTimer.Start();
    }

    private void PlayStart() { if (Settings.PlaySounds) Sounds.PlayStart(); }
    private void PlayStop() { if (Settings.PlaySounds) Sounds.PlayStop(); }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _levelTimer.Stop();
        _permissionTimer.Stop();
        _hook.Dispose();
        if (Status == StatusKind.Recording) _ = _recorder.Stop();
        _media.ResumeIfPausedAsync().Wait(500);
        Models.Shutdown();
    }
}
