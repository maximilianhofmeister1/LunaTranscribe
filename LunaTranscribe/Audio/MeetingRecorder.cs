using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LunaTranscribe.Output;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LunaTranscribe.Audio;

/// <summary>
/// Aufnahme des Meeting-Modus: Mikrofon (WASAPI, Shared Mode) plus optional Systemaudio (WASAPI-Loopback
/// des Standard-Ausgabegeräts), beide live nach 16 kHz / mono gewandelt, je Quelle gemittelt und summiert
/// (wie <c>channelGroups</c> der macOS-Version) und direkt als 16-Bit-WAV auf die Platte geschrieben
/// (≈ 1,9 MB/min, RAM bleibt konstant).
///
/// Das Mikrofon ist die Uhr: Jedes Mikrofon-Paket schreibt genau so viele Samples, wie es liefert, und
/// holt sich dieselbe Menge Systemaudio aus einem Jitter-Puffer. Loopback liefert unter Windows keine
/// Pakete, solange nichts abgespielt wird – dann wird Stille gemischt. Der Puffer ist nach oben begrenzt
/// (Uhren-Drift, Rückstau), sodass die Datei nie aus dem Takt läuft und nicht unbegrenzt wächst.
/// Geräte werden beim Start festgelegt. Läuft parallel zum Diktat-<see cref="AudioRecorder"/>.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    public const int SampleRate = 16_000;

    /// <summary>Puffergröße der WASAPI-Clients: groß und gepollt statt ereignisgesteuert = wenig CPU.</summary>
    private const int CaptureBufferMs = 100;
    /// <summary>Alle so viele Sekunden wird der WAV-Header aktualisiert – nach einem Absturz bleibt die Datei lesbar.</summary>
    private const double HeaderUpdateSeconds = 5;

    private readonly object _lock = new();
    private WasapiCapture? _mic;
    private MMDevice? _micDevice;
    private WasapiLoopbackCapture? _loopback;
    private MMDevice? _renderDevice;
    private SourceConverter? _micConverter;
    private SourceConverter? _systemConverter;
    private SystemAudioBuffer? _systemBuffer;
    private WaveFileWriter? _writer;
    private float[] _mix = new float[4096];
    private float[] _sys = new float[4096];
    private long _frames;
    private long _lastHeaderUpdate;
    private volatile bool _stopping;

    /// <summary>Pfad der laufenden bzw. zuletzt beendeten Aufnahme.</summary>
    public string? OutputPath { get; private set; }

    public bool IsRecording => _mic != null && !_stopping;

    /// <summary>Systemaudio wird tatsächlich mitgeschnitten (false = nur Mikrofon, z. B. nach Loopback-Fehler).</summary>
    public bool SystemAudioActive { get; private set; }

    /// <summary>Bisher aufgenommene Dauer (Mikrofon-Zeitachse = Länge der Datei).</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(RecordedSeconds);

    public double RecordedSeconds => Interlocked.Read(ref _frames) / (double)SampleRate;

    /// <summary>Aktueller Spitzenpegel 0…1 der Mischung (Pegelanzeige).</summary>
    public float Level { get; private set; }

    /// <summary>Aktueller Pegel je Quelle 0…1 (Diagnose).</summary>
    public float MicLevel { get; private set; }
    public float SystemLevel => _systemBuffer?.Level ?? 0;

    /// <summary>Höchster Pegel je Quelle seit Aufnahmestart (Diagnose: kommt Systemaudio an?).</summary>
    public float MicPeak { get; private set; }
    public float SystemPeak => _systemBuffer?.Peak ?? 0;

    /// <summary>Wird einmal pro Aufnahme aufgerufen, sobald das erste Mikrofon-Paket ankommt (Diagnose).</summary>
    public Action? OnFirstBuffer { get; set; }

    /// <summary>Das Mikrofon ist während der Aufnahme ausgefallen (z. B. abgesteckt). Die Datei bleibt bis dahin gültig.</summary>
    public event Action<Exception>? MicrophoneFailed;

    /// <summary>
    /// Startet die Aufnahme nach <paramref name="outputPath"/> (wird überschrieben).
    /// </summary>
    /// <param name="micDeviceUid">leer/null = System-Standardmikrofon</param>
    /// <param name="captureSystemAudio">Systemaudio mitschneiden; schlägt Loopback fehl, läuft die Aufnahme nur mit Mikrofon.</param>
    public void Start(string? micDeviceUid, bool captureSystemAudio, string outputPath)
    {
        if (_mic != null) throw new InvalidOperationException("Die Meeting-Aufnahme läuft bereits.");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        MMDevice? device = null;
        if (!string.IsNullOrEmpty(micDeviceUid)) device = AudioDevices.Device(micDeviceUid);
        device ??= AudioDevices.DefaultDevice() ?? throw new AudioRecorderException(AudioRecorderErrorKind.NoInput);

        WasapiCapture mic;
        SourceConverter micConverter;
        try
        {
            mic = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: CaptureBufferMs);
            micConverter = new SourceConverter(mic.WaveFormat);
        }
        catch (Exception ex)
        {
            device.Dispose();
            throw ex is AudioRecorderException are ? are : Translate(ex);
        }

        WaveFileWriter writer;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            writer = new WaveFileWriter(outputPath, new WaveFormat(SampleRate, 16, 1));
        }
        catch (Exception ex)
        {
            mic.Dispose(); device.Dispose();
            throw new IOException($"Aufnahmedatei konnte nicht angelegt werden: {outputPath}", ex);
        }

        lock (_lock)
        {
            _frames = 0;
            _lastHeaderUpdate = 0;
            _writer = writer;
            _micConverter = micConverter;
            _systemBuffer = new SystemAudioBuffer();
        }
        _stopping = false;
        _firstBufferSeen = false;
        Level = 0; MicLevel = 0; MicPeak = 0;
        OutputPath = outputPath;
        SystemAudioActive = false;

        // Systemaudio zuerst, damit von Beginn an mitgeschnitten wird; Fehler sind nicht fatal.
        if (captureSystemAudio) StartLoopback();

        mic.DataAvailable += OnMicData;
        mic.RecordingStopped += OnMicStopped;
        try
        {
            mic.StartRecording();
        }
        catch (Exception ex)
        {
            mic.DataAvailable -= OnMicData;
            mic.RecordingStopped -= OnMicStopped;
            mic.Dispose(); device.Dispose();
            StopLoopback();
            lock (_lock) { _writer?.Dispose(); _writer = null; }
            TryDelete(outputPath);
            throw Translate(ex);
        }
        _mic = mic;
        _micDevice = device;
        Log.Write($"meeting recorder started in {sw.ElapsedMilliseconds} ms: mic {mic.WaveFormat}, systemAudio={SystemAudioActive}");
    }

    /// <summary>Beendet die Aufnahme, schließt die Datei und gibt die aufgenommene Dauer in Sekunden zurück.</summary>
    public double Stop()
    {
        if (_mic == null) return RecordedSeconds;
        _stopping = true;

        // Loopback zuerst, dann Mikrofon. Dispose wartet auf die Capture-Threads – nicht unter _lock.
        StopLoopback();
        var mic = _mic;
        _mic = null;
        mic.DataAvailable -= OnMicData;
        mic.RecordingStopped -= OnMicStopped;
        try { mic.StopRecording(); } catch { }
        mic.Dispose();
        _micDevice?.Dispose();
        _micDevice = null;

        lock (_lock)
        {
            // Resampler-Rest des Mikrofons noch schreiben.
            if (_micConverter != null)
            {
                var n = _micConverter.Flush();
                if (n > 0) WriteMixed(_micConverter.Output, n);
            }
            _writer?.Dispose();   // schreibt den endgültigen Header
            _writer = null;
            _micConverter = null;
        }
        Level = 0; MicLevel = 0;
        var seconds = RecordedSeconds;
        Log.Write($"meeting recorder stopped: {seconds:0.0} s, peaks mic {MicPeak:0.000} / system {SystemPeak:0.000}" +
                  $", system underruns {_systemBuffer?.Underruns ?? 0}, drops {_systemBuffer?.DroppedSamples ?? 0} samples");
        return seconds;
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
    }

    // MARK: Systemaudio

    private void StartLoopback()
    {
        MMDevice? render = null;
        WasapiLoopbackCapture? loopback = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator())
            {
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console))
                    throw new InvalidOperationException("kein Ausgabegerät");
                render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            }
            loopback = new WasapiLoopbackCapture(render);
            var converter = new SourceConverter(loopback.WaveFormat);
            lock (_lock) _systemConverter = converter;
            loopback.DataAvailable += OnLoopbackData;
            loopback.RecordingStopped += OnLoopbackStopped;
            loopback.StartRecording();
            _loopback = loopback;
            _renderDevice = render;
            SystemAudioActive = true;
            Log.Write($"meeting loopback started: {render.FriendlyName} ({loopback.WaveFormat})");
        }
        catch (Exception ex)
        {
            // Kein harter Fehler: Das Meeting läuft mit Mikrofon weiter, die UI zeigt den Hinweis.
            Log.Write("meeting loopback unavailable, falling back to microphone: " + ex.Message);
            if (loopback != null)
            {
                loopback.DataAvailable -= OnLoopbackData;
                loopback.RecordingStopped -= OnLoopbackStopped;
                try { loopback.Dispose(); } catch { }
            }
            render?.Dispose();
            lock (_lock) _systemConverter = null;
            SystemAudioActive = false;
        }
    }

    private void StopLoopback()
    {
        var loopback = _loopback;
        _loopback = null;
        if (loopback != null)
        {
            loopback.DataAvailable -= OnLoopbackData;
            loopback.RecordingStopped -= OnLoopbackStopped;
            try { loopback.StopRecording(); } catch { }
            loopback.Dispose();
        }
        _renderDevice?.Dispose();
        _renderDevice = null;
        lock (_lock) _systemConverter = null;
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        if (_stopping || e.BytesRecorded == 0) return;
        SourceConverter? converter;
        SystemAudioBuffer? buffer;
        lock (_lock) { converter = _systemConverter; buffer = _systemBuffer; }
        if (converter == null || buffer == null) return;
        // Eigener Konverter je Quelle: läuft auf dem Loopback-Thread, nur der Puffer ist geteilt.
        var n = converter.Convert(e.Buffer, e.BytesRecorded);
        if (n > 0) buffer.Write(converter.Output, n);
    }

    private void OnLoopbackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null || _stopping) return;
        // Ausgabegerät weg (abgesteckt/gewechselt): ab hier nur Mikrofon, die Datei bleibt im Takt.
        Log.Write("meeting loopback stopped unexpectedly: " + e.Exception.Message);
        SystemAudioActive = false;
    }

    // MARK: Mikrofon (Taktgeber)

    private bool _firstBufferSeen;

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_stopping || e.BytesRecorded == 0) return;
        lock (_lock)
        {
            if (_micConverter == null || _writer == null) return;
            var n = _micConverter.Convert(e.Buffer, e.BytesRecorded);
            if (n <= 0) return;
            WriteMixed(_micConverter.Output, n);
        }
        if (!_firstBufferSeen)
        {
            _firstBufferSeen = true;
            OnFirstBuffer?.Invoke();
        }
    }

    /// <summary>Mischt n Mikrofon-Samples mit n Systemaudio-Samples und schreibt sie. Muss unter _lock laufen.</summary>
    private void WriteMixed(float[] mic, int n)
    {
        if (_writer == null) return;
        if (_mix.Length < n) { _mix = new float[n]; _sys = new float[n]; }
        _systemBuffer?.Read(_sys, n);

        float micPeak = 0, peak = 0;
        for (var i = 0; i < n; i++)
        {
            var m = mic[i];
            var am = Math.Abs(m);
            if (am > micPeak) micPeak = am;
            var v = Math.Clamp(m + _sys[i], -1f, 1f);
            var a = Math.Abs(v);
            if (a > peak) peak = a;
            _mix[i] = v;
        }
        _writer.WriteSamples(_mix, 0, n);
        Interlocked.Add(ref _frames, n);

        MicLevel = Math.Min(1f, micPeak);
        if (MicLevel > MicPeak) MicPeak = MicLevel;
        Level = peak;

        if (_frames - _lastHeaderUpdate >= HeaderUpdateSeconds * SampleRate)
        {
            _lastHeaderUpdate = _frames;
            try { _writer.Flush(); } catch (Exception ex) { Log.Write("meeting wav flush failed: " + ex.Message); }
        }
    }

    private void OnMicStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null || _stopping) return;
        Log.Write("meeting microphone stopped unexpectedly: " + e.Exception.Message);
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { }
        }
        MicrophoneFailed?.Invoke(e.Exception);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static AudioRecorderException Translate(Exception ex)
    {
        if (ex is COMException com)
        {
            switch ((uint)com.HResult)
            {
                case 0x80070005: return new AudioRecorderException(AudioRecorderErrorKind.AccessDenied, ex);
                case 0x8889000A: return new AudioRecorderException(AudioRecorderErrorKind.DeviceInUse, ex);
                case 0x88890004: return new AudioRecorderException(AudioRecorderErrorKind.NoInput, ex);
            }
        }
        if (ex is UnauthorizedAccessException) return new AudioRecorderException(AudioRecorderErrorKind.AccessDenied, ex);
        return new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable, ex);
    }

    /// <summary>Gerätformat → Float → mono (Mittelwert der Kanäle = eine Kanalgruppe) → 16 kHz.</summary>
    private sealed class SourceConverter
    {
        private readonly WaveFormat _format;
        private readonly BufferedWaveProvider _buffered;
        private readonly ISampleProvider _pipeline;
        private float[] _output = new float[4096];

        /// <summary>Konvertierte Samples des letzten Aufrufs (gültig bis zum nächsten).</summary>
        public float[] Output => _output;

        public SourceConverter(WaveFormat format)
        {
            if (format.SampleRate <= 0 || format.Channels <= 0)
                throw new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable);
            _format = format;
            _buffered = new BufferedWaveProvider(format)
            {
                ReadFully = false,
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5),
            };
            ISampleProvider sp;
            try { sp = _buffered.ToSampleProvider(); }
            catch (Exception ex) { throw new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable, ex); }
            if (format.Channels > 1) sp = new MonoMixSampleProvider(sp);
            if (format.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
            _pipeline = sp;
        }

        /// <summary>Nimmt Rohdaten auf und liefert die Anzahl konvertierter Samples in <see cref="Output"/>.</summary>
        public int Convert(byte[] data, int bytes)
        {
            _buffered.AddSamples(data, 0, bytes);
            return Drain(flush: false);
        }

        public int Flush() => Drain(flush: true);

        private int Drain(bool flush)
        {
            var inFrames = _buffered.BufferedBytes / _format.BlockAlign;
            var want = (int)(inFrames * (double)SampleRate / _format.SampleRate) + (flush ? 4096 : 64);
            var total = 0;
            while (true)
            {
                if (_output.Length < total + want) Array.Resize(ref _output, total + want);
                var n = _pipeline.Read(_output, total, want);
                if (n <= 0) break;
                total += n;
                if (!flush || n < want) break;
            }
            return total;
        }
    }

    /// <summary>
    /// Jitter-Puffer für das Systemaudio (16 kHz mono). Der Loopback-Thread schreibt, der Mikrofon-Thread
    /// liest im Takt des Mikrofons. Nach einer Lücke wird erst wieder gelesen, wenn <see cref="Prebuffer"/>
    /// Samples bereitliegen (glättet Paket-Jitter); wird der Rückstand größer als <see cref="MaxBacklog"/>
    /// (Drift der Gerätetakte, hängender Mikrofon-Thread), wird auf <see cref="Prebuffer"/> gekürzt.
    /// So bleibt der Versatz zwischen den Quellen immer unter ≈ 0,3 s und der Speicher konstant.
    /// </summary>
    private sealed class SystemAudioBuffer
    {
        private const int Prebuffer = SampleRate * 120 / 1000;   // 120 ms
        private const int MaxBacklog = SampleRate * 320 / 1000;  // 320 ms
        private const int Capacity = SampleRate * 2;             // 2 s Ringpuffer

        private readonly object _lock = new();
        private readonly float[] _ring = new float[Capacity];
        private int _read;
        private int _count;
        private bool _playing;

        public float Level { get; private set; }
        public float Peak { get; private set; }
        public int Underruns { get; private set; }
        public long DroppedSamples { get; private set; }

        public void Write(float[] src, int n)
        {
            float peak = 0;
            for (var i = 0; i < n; i++)
            {
                var a = Math.Abs(src[i]);
                if (a > peak) peak = a;
            }
            Level = Math.Min(1f, peak);
            if (Level > Peak) Peak = Level;

            lock (_lock)
            {
                var offset = 0;
                if (n > Capacity) { offset = n - Capacity; DroppedSamples += offset; n = Capacity; }
                var overflow = _count + n - Capacity;
                if (overflow > 0) Skip(overflow);
                var write = (_read + _count) % Capacity;
                var first = Math.Min(n, Capacity - write);
                Array.Copy(src, offset, _ring, write, first);
                if (n > first) Array.Copy(src, offset + first, _ring, 0, n - first);
                _count += n;

                if (_count > MaxBacklog) Skip(_count - Prebuffer);
            }
        }

        /// <summary>Füllt dst[0..n) mit Systemaudio bzw. Stille.</summary>
        public void Read(float[] dst, int n)
        {
            lock (_lock)
            {
                if (!_playing && _count >= Prebuffer) _playing = true;
                var take = _playing ? Math.Min(n, _count) : 0;
                var first = Math.Min(take, Capacity - _read);
                Array.Copy(_ring, _read, dst, 0, first);
                if (take > first) Array.Copy(_ring, 0, dst, first, take - first);
                _read = (_read + take) % Capacity;
                _count -= take;
                if (take < n)
                {
                    Array.Clear(dst, take, n - take);
                    if (_playing) { _playing = false; Underruns++; }
                }
            }
            if (!_playing) Level = 0;
        }

        private void Skip(int samples)
        {
            samples = Math.Min(samples, _count);
            _read = (_read + samples) % Capacity;
            _count -= samples;
            DroppedSamples += samples;
        }
    }
}
