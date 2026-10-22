using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Audio;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Meeting;

public enum MeetingStatusKind { Idle, Recording, Failed }

/// <summary>
/// Aufnahme des Meeting-Modus (Port von MeetingSession.swift). Läuft unabhängig vom Diktat: eigener Recorder,
/// eigene Statusmaschine, kein Overlay. Während des Meetings wird nur aufgenommen; „Beenden“ übergibt die
/// Aufnahme an die <see cref="MeetingProcessingQueue"/> und ist sofort wieder bereit für das nächste Meeting.
/// Läuft auf dem UI-Thread.
/// </summary>
public sealed class MeetingSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly SettingsStore _settings;
    private readonly MeetingProcessingQueue _queue;
    private readonly DispatcherTimer _elapsedTimer;
    private MeetingRecorder? _recorder;
    private string? _audioPath;
    private DateTime? _recordingStart;
    private MeetingStatusKind _status = MeetingStatusKind.Idle;
    private string _failure = "";
    private TimeSpan _elapsed;
    private bool _systemAudioActive;

    public MeetingSession(SettingsStore settings, MeetingProcessingQueue queue, Dispatcher dispatcher)
    {
        _settings = settings;
        _queue = queue;
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
        Directory.CreateDirectory(AppPaths.MeetingRecordings);
        var path = Path.Combine(AppPaths.MeetingRecordings, Guid.NewGuid().ToString("D").ToUpperInvariant() + ".wav");
        var recorder = new MeetingRecorder();
        recorder.MicrophoneFailed += ex => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Mikrofon weg (abgesteckt): bis hierher Aufgenommenes verarbeiten statt verwerfen.
            Log.Write("meeting microphone failed: " + ex.Message);
            if (IsRecording) Stop();
        });
        try
        {
            recorder.Start(_settings.InputDeviceUid, _settings.MeetingCaptureSystemAudio, path);
        }
        catch (AudioRecorderException ex)
        {
            recorder.Dispose();
            TryDelete(path);
            Fail(ex.Message);
            if (ex.Kind == AudioRecorderErrorKind.AccessDenied) Permissions.OpenMicrophoneSettings();
            return;
        }
        catch (Exception ex)
        {
            recorder.Dispose();
            TryDelete(path);
            Fail("Aufnahme konnte nicht gestartet werden: " + ex.Message);
            return;
        }
        _recorder = recorder;
        _audioPath = path;
        _recordingStart = DateTime.Now;
        SystemAudioActive = recorder.SystemAudioActive;
        Elapsed = TimeSpan.Zero;
        Status = MeetingStatusKind.Recording;
        _elapsedTimer.Start();
        Log.Write($"meeting recording started, systemAudio={SystemAudioActive}");
    }

    /// <summary>
    /// Beendet die Aufnahme und reiht sie zur Verarbeitung im Hintergrund ein. Jeder Ausgang ist sichtbar
    /// (Status + Log), damit „Beenden“ nie stumm verpufft.
    /// </summary>
    public void Stop()
    {
        if (!IsRecording || _audioPath is not { } path || _recordingStart is not { } start) return;
        var duration = StopCapture();
        Log.Write($"meeting stopped after {duration:0.0} s");
        if (duration < 1)
        {
            Fail($"Es kam kein Audio an ({duration:0.0} s aufgenommen). Mikrofon-Datenschutz prüfen.");
            return;
        }
        try
        {
            _queue.Enqueue(path, start.ToUniversalTime(), duration, _settings.LanguageForEngine(_settings.PrimaryLanguage), _settings.MeetingSpeakerCount);
            _audioPath = null;   // gehört jetzt der Warteschlange
            _recordingStart = null;
            Status = MeetingStatusKind.Idle;
        }
        catch (Exception ex)
        {
            // Aufnahme bleibt im Aufnahmeordner und wird beim nächsten Start gerettet.
            _audioPath = null;
            Failure = "Die Aufnahme konnte nicht zur Verarbeitung abgelegt werden: " + ex.Message;
            Status = MeetingStatusKind.Failed;
            Log.Write("meeting enqueue failed: " + ex);
        }
    }

    /// <summary>Bricht die Aufnahme ab und verwirft sie.</summary>
    public void Cancel()
    {
        if (IsRecording) StopCapture();
        DiscardAudio();
        Status = MeetingStatusKind.Idle;
    }

    public void DismissError()
    {
        if (Status == MeetingStatusKind.Failed) Status = MeetingStatusKind.Idle;
    }

    /// <summary>
    /// Aufnahmen, die beim Absturz oder Beenden noch liefen, in die Warteschlange retten (die WAV-Kopfdaten werden
    /// alle 5 s aktualisiert, die Datei ist also lesbar). Abweichung von macOS, das solche Reste löscht: unter Windows
    /// gilt auch hier „eine Aufnahme geht nie verloren“.
    /// </summary>
    public void RescueOrphanedRecordings()
    {
        if (!Directory.Exists(AppPaths.MeetingRecordings)) return;
        foreach (var file in Directory.GetFiles(AppPaths.MeetingRecordings, "*.wav"))
        {
            if (file == _audioPath) continue;
            try
            {
                var seconds = WavFile.DurationSeconds(file);
                if (seconds < 1) { File.Delete(file); continue; }
                _queue.Enqueue(file, File.GetCreationTimeUtc(file), seconds, _settings.LanguageForEngine(_settings.PrimaryLanguage), 0);
                Log.Write($"meeting recording rescued ({seconds:0} s)");
            }
            catch (Exception ex)
            {
                Log.Write("meeting rescue failed: " + ex.Message);
            }
        }
    }

    // MARK: Intern

    private double StopCapture()
    {
        _elapsedTimer.Stop();
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
        DiscardAudio();
        Failure = message;
        Status = MeetingStatusKind.Failed;
        Log.Write("meeting failed: " + message);
    }

    private void DiscardAudio()
    {
        if (_audioPath != null) TryDelete(_audioPath);
        _audioPath = null;
        _recordingStart = null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Beim Beenden der App: laufende Aufnahme ordentlich abschließen und einreihen.</summary>
    public void Shutdown()
    {
        if (IsRecording) Stop();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
