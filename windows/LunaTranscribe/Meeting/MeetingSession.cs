using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Audio;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;
using Microsoft.Win32;

namespace LunaTranscribe.Meeting;

public enum MeetingStatusKind { Idle, Recording, Failed }

/// <summary>
/// Aufnahme des Meeting-Modus (Port von MeetingSession.swift). Läuft unabhängig vom Diktat: eigener Recorder,
/// eigene Statusmaschine, kein Overlay. Während des Meetings wird nur aufgenommen; „Beenden“ übergibt die
/// Aufnahme an die <see cref="MeetingProcessingQueue"/> und ist sofort wieder bereit für das nächste Meeting.
/// Läuft auf dem UI-Thread.
///
/// Absturzsicher: Die Aufnahme schreibt von Anfang an in einen Auftragsordner der Warteschlange
/// (<see cref="MeetingProcessingQueue.BeginRecording"/>), die WAV trägt ihren Kopf alle paar Sekunden nach. Beendet sich
/// die App, meldet sich der Nutzer ab, fährt der PC herunter oder geht er in den Energiesparmodus, wird ein laufendes
/// Meeting regulär beendet und eingereiht.
/// </summary>
public sealed class MeetingSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Wer die Aufnahme beendet. Bei Systemereignissen gibt es keinen Fehlerstatus – das Beenden bzw. Herunterfahren
    /// soll ohne Rückfrage durchlaufen.
    /// </summary>
    private enum StopReason { User, AppTerminating, PowerOff, Sleep }

    private readonly SettingsStore _settings;
    private readonly MeetingProcessingQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _elapsedTimer;
    private MeetingRecorder? _recorder;
    /// <summary>Entwurf der laufenden Aufnahme in der Warteschlange.</summary>
    private MeetingRecordingDraft? _draft;
    private DateTime? _recordingStart;
    private MeetingStatusKind _status = MeetingStatusKind.Idle;
    private string _failure = "";
    private TimeSpan _elapsed;
    private bool _systemAudioActive;
    private bool _observingSystem;

    public MeetingSession(SettingsStore settings, MeetingProcessingQueue queue, Dispatcher dispatcher)
    {
        _settings = settings;
        _queue = queue;
        _dispatcher = dispatcher;
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => { if (_recordingStart is { } start) Elapsed = DateTime.Now - start; };
    }

    public MeetingStatusKind Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsRecording)); }
    }

    public bool IsRecording => Status == MeetingStatusKind.Recording;

    /// <summary>Fehlertext, solange <see cref="Status"/> = Failed.</summary>
    public string Failure
    {
        get => _failure;
        private set { _failure = value; OnPropertyChanged(); }
    }

    /// <summary>Laufzeit der aktuellen Aufnahme, einmal pro Sekunde aktualisiert.</summary>
    public TimeSpan Elapsed
    {
        get => _elapsed;
        private set { _elapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedText)); }
    }

    public string ElapsedText => MeetingTranscript.Clock(Elapsed.TotalSeconds);

    /// <summary>Systemaudio wurde für die laufende Aufnahme tatsächlich eingebunden.</summary>
    public bool SystemAudioActive
    {
        get => _systemAudioActive;
        private set { if (_systemAudioActive == value) return; _systemAudioActive = value; OnPropertyChanged(); }
    }

    // MARK: Start / Stopp

    public void Start()
    {
        if (Status == MeetingStatusKind.Recording) return;

        MeetingRecordingDraft draft;
        try
        {
            // Sprache/Sprecherzahl vom Start – gelten nur, falls die Aufnahme unterbrochen wird.
            draft = _queue.BeginRecording(DateTime.UtcNow, CurrentLanguage, _settings.MeetingSpeakerCount);
        }
        catch (Exception ex)
        {
            Fail("Die Aufnahme konnte nicht angelegt werden: " + ex.Message);
            return;
        }

        var recorder = new MeetingRecorder();
        recorder.MicrophoneFailed += ex => _dispatcher.BeginInvoke(() =>
        {
            // Mikrofon weg (abgesteckt): bis hierher Aufgenommenes verarbeiten statt verwerfen.
            Log.Write("meeting microphone failed: " + ex.Message);
            if (IsRecording) Stop();
        });
        try
        {
            recorder.Start(_settings.InputDeviceUid, _settings.MeetingCaptureSystemAudio, draft.AudioPath);
        }
        catch (AudioRecorderException ex)
        {
            recorder.Dispose();
            _queue.DiscardRecording(draft);
            Fail(ex.Message);
            if (ex.Kind == AudioRecorderErrorKind.AccessDenied) Permissions.OpenMicrophoneSettings();
            return;
        }
        catch (Exception ex)
        {
            recorder.Dispose();
            _queue.DiscardRecording(draft);
            Fail("Aufnahme konnte nicht gestartet werden: " + ex.Message);
            return;
        }
        _recorder = recorder;
        _draft = draft;
        _recordingStart = DateTime.Now;
        SystemAudioActive = recorder.SystemAudioActive;
        Elapsed = TimeSpan.Zero;
        Status = MeetingStatusKind.Recording;
        _elapsedTimer.Start();
        ObserveSystemEvents(true);
        Log.Write($"meeting recording started, systemAudio={SystemAudioActive}");
    }

    /// <summary>
    /// Beendet die Aufnahme und reiht sie zur Verarbeitung im Hintergrund ein. Jeder Ausgang ist sichtbar
    /// (Status + Log), damit „Beenden“ nie stumm verpufft.
    /// </summary>
    public void Stop() => Stop(StopReason.User);

    private void Stop(StopReason reason)
    {
        if (!IsRecording || _draft is not { } draft) return;
        var duration = StopCapture();
        _draft = null;
        _recordingStart = null;
        Log.Write($"meeting stopped ({reason}) after {duration:0.0} s");
        if (duration < RecordingRecovery.MinimumDuration)
        {
            _queue.DiscardRecording(draft);
            if (reason == StopReason.User)
                Fail($"Es kam kein Audio an ({duration:0.0} s aufgenommen). Mikrofon-Datenschutz prüfen.");
            else
                Status = MeetingStatusKind.Idle;
            return;
        }
        // Kann nicht scheitern: Klappt das Schreiben nicht, bleibt die Aufnahme als unterbrochene erhalten.
        _queue.FinishRecording(draft, duration, CurrentLanguage, _settings.MeetingSpeakerCount);
        Status = MeetingStatusKind.Idle;
    }

    /// <summary>Bricht die Aufnahme ab und verwirft sie.</summary>
    public void Cancel()
    {
        if (IsRecording) StopCapture();
        if (_draft != null) _queue.DiscardRecording(_draft);
        _draft = null;
        _recordingStart = null;
        Status = MeetingStatusKind.Idle;
    }

    public void DismissError()
    {
        if (Status == MeetingStatusKind.Failed) Status = MeetingStatusKind.Idle;
    }

    /// <summary>Beim Beenden der App: laufende Aufnahme ordentlich abschließen und einreihen.</summary>
    public void Shutdown()
    {
        Stop(StopReason.AppTerminating);
        ObserveSystemEvents(false);
    }

    // MARK: Systemereignisse

    /// <summary>
    /// Abmelden/Herunterfahren und Energiesparmodus beenden ein laufendes Meeting regulär – schnell und ohne Rückfrage.
    /// Die Ereignisse kommen auf dem SystemEvents-Thread; <see cref="Dispatcher.Invoke(Action)"/> wartet, bis die Aufnahme
    /// abgeschlossen ist, bevor Windows weitermacht. Im Energiesparmodus liefern die Geräte ohnehin nichts, danach sind
    /// sie oft neu verbunden. Nur während einer Aufnahme abonniert (SystemEvents hält sonst Referenzen fest).
    /// </summary>
    private void ObserveSystemEvents(bool on)
    {
        if (on == _observingSystem) return;
        _observingSystem = on;
        try
        {
            if (on)
            {
                SystemEvents.SessionEnding += OnSessionEnding;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            else
            {
                SystemEvents.SessionEnding -= OnSessionEnding;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
        }
        catch (Exception ex) { Log.Write("system events: " + ex.Message); }
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => StopFromSystem(StopReason.PowerOff);

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) StopFromSystem(StopReason.Sleep);
    }

    private void StopFromSystem(StopReason reason)
    {
        try
        {
            if (_dispatcher.CheckAccess()) Stop(reason);
            else _dispatcher.Invoke(() => Stop(reason), DispatcherPriority.Send, System.Threading.CancellationToken.None, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { Log.Write($"meeting stop ({reason}) failed: " + ex.Message); }
    }

    // MARK: Intern

    private AppLanguage CurrentLanguage => _settings.LanguageForEngine(_settings.PrimaryLanguage);

    /// <summary>Stoppt den Recorder (WAV wird mit vollständigem Kopf geschlossen) und gibt die Dauer zurück.</summary>
    private double StopCapture()
    {
        _elapsedTimer.Stop();
        ObserveSystemEvents(false);
        var recorder = _recorder;
        _recorder = null;
        if (recorder == null) return 0;
        double seconds;
        try { seconds = recorder.Stop(); }
        catch (Exception ex) { Log.Write("meeting stop failed: " + ex.Message); seconds = recorder.RecordedSeconds; }
        recorder.Dispose();
        return seconds;
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = MeetingStatusKind.Failed;
        Log.Write("meeting failed: " + message);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
