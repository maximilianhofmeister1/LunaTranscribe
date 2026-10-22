using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LunaTranscribe.Audio;

public enum AudioRecorderErrorKind { NoInput, AccessDenied, FormatUnavailable, DeviceInUse }

public sealed class AudioRecorderException : Exception
{
    public AudioRecorderErrorKind Kind { get; }

    public AudioRecorderException(AudioRecorderErrorKind kind, Exception? inner = null)
        : base(Describe(kind), inner) => Kind = kind;

    private static string Describe(AudioRecorderErrorKind kind) => kind switch
    {
        AudioRecorderErrorKind.NoInput => "Kein Mikrofon gefunden.",
        AudioRecorderErrorKind.AccessDenied => "Kein Mikrofon-Zugriff. Bitte in den Windows-Einstellungen erlauben.",
        AudioRecorderErrorKind.FormatUnavailable => "Audioformat konnte nicht eingerichtet werden.",
        AudioRecorderErrorKind.DeviceInUse => "Das Mikrofon wird gerade exklusiv von einem anderen Programm verwendet.",
        _ => "Aufnahmefehler.",
    };
}

/// <summary>
/// Nimmt vom Mikrofon (WASAPI, Shared Mode) auf und wandelt live nach 16 kHz / mono / Float32.
/// Die Samples bleiben ausschließlich im RAM und werden mit <see cref="Stop"/> herausgegeben und verworfen.
/// </summary>
public sealed class AudioRecorder
{
    public const int TargetSampleRate = 16_000;

    private readonly object _lock = new();
    private readonly List<float> _samples = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffered;
    private ISampleProvider? _pipeline;
    private WaveFormat? _format;
    private float[] _tmp = new float[8192];
    private bool _stopping;
    private bool _firstBufferSeen;

    /// <summary>Aktueller Spitzenpegel 0…1 (für die Pegelanzeige im Overlay).</summary>
    public float Level { get; private set; }

    /// <summary>Wird einmal pro Aufnahme aufgerufen, sobald das erste Audio-Paket ankommt (Diagnose).</summary>
    public Action? OnFirstBuffer { get; set; }

    public bool IsRecording => _capture != null && !_stopping;

    /// <param name="deviceUid">leer/null = System-Standardmikrofon</param>
    public void Start(string? deviceUid)
    {
        // Pro Aufnahme eine frische Capture-Instanz: folgt so automatisch dem aktuellen Standardgerät.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MMDevice? device = null;
        if (!string.IsNullOrEmpty(deviceUid)) device = AudioDevices.Device(deviceUid);
        device ??= AudioDevices.DefaultDevice() ?? throw new AudioRecorderException(AudioRecorderErrorKind.NoInput);

        LunaTranscribe.Output.Log.Write($"  device resolved +{sw.ElapsedMilliseconds}ms");
        WasapiCapture capture;
        try
        {
            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 40);
            LunaTranscribe.Output.Log.Write($"  capture created +{sw.ElapsedMilliseconds}ms ({capture.WaveFormat})");
        }
        catch (Exception ex)
        {
            device.Dispose();
            throw Translate(ex);
        }

        var format = capture.WaveFormat;
        if (format.SampleRate <= 0 || format.Channels <= 0)
        {
            capture.Dispose(); device.Dispose();
            throw new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable);
        }

        // Pipeline: Gerätformat → Float → Mono → 16 kHz
        var buffered = new BufferedWaveProvider(format)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(10),
        };
        ISampleProvider sp;
        try
        {
            sp = buffered.ToSampleProvider();
        }
        catch (Exception ex)
        {
            capture.Dispose(); device.Dispose();
            throw new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable, ex);
        }
        if (format.Channels > 1) sp = new MonoMixSampleProvider(sp);
        if (format.SampleRate != TargetSampleRate) sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);

        lock (_lock) _samples.Clear();
        Level = 0;
        _firstBufferSeen = false;
        _stopping = false;
        _format = format;
        _buffered = buffered;
        _pipeline = sp;
        _device = device;

        capture.DataAvailable += OnDataAvailable;
        try
        {
            capture.StartRecording();
            LunaTranscribe.Output.Log.Write($"  StartRecording returned +{sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.Dispose(); device.Dispose();
            _buffered = null; _pipeline = null; _device = null;
            throw Translate(ex);
        }
        _capture = capture;
    }

    /// <summary>Stoppt die Aufnahme und gibt die Samples zurück. Der interne Puffer wird geleert.</summary>
    public float[] Stop()
    {
        var capture = _capture;
        _capture = null;
        _stopping = true;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        _device?.Dispose();
        _device = null;

        float[] result;
        lock (_lock)
        {
            // Resampler-Rest herausholen
            Drain(flush: true);
            result = _samples.ToArray();
            _samples.Clear();
        }
        _buffered = null;
        _pipeline = null;
        Level = 0;
        return result;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stopping || e.BytesRecorded == 0) return;
        lock (_lock)
        {
            if (_buffered == null || _pipeline == null || _format == null) return;
            _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Drain(flush: false);
        }
    }

    /// <summary>Zieht alle verfügbaren, konvertierten Samples aus der Pipeline. Muss unter _lock laufen.</summary>
    private void Drain(bool flush)
    {
        if (_pipeline == null || _buffered == null || _format == null) return;
        var inFrames = _buffered.BufferedBytes / _format.BlockAlign;
        var want = (int)(inFrames * (double)TargetSampleRate / _format.SampleRate) + (flush ? 4096 : 64);
        if (want <= 0) return;
        if (_tmp.Length < want) _tmp = new float[want];

        var total = 0;
        float peak = 0;
        while (true)
        {
            var n = _pipeline.Read(_tmp, 0, want);
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                var a = Math.Abs(_tmp[i]);
                if (a > peak) peak = a;
                _samples.Add(_tmp[i]);
            }
            total += n;
            if (!flush || n < want) break;
        }

        if (total > 0)
        {
            Level = Math.Min(1f, peak);
            if (!_firstBufferSeen)
            {
                _firstBufferSeen = true;
                OnFirstBuffer?.Invoke();
            }
        }
    }

    private static AudioRecorderException Translate(Exception ex)
    {
        if (ex is COMException com)
        {
            switch ((uint)com.HResult)
            {
                case 0x80070005: // E_ACCESSDENIED – Datenschutz: Mikrofonzugriff für Desktop-Apps aus
                    return new AudioRecorderException(AudioRecorderErrorKind.AccessDenied, ex);
                case 0x8889000A: // AUDCLNT_E_DEVICE_IN_USE
                    return new AudioRecorderException(AudioRecorderErrorKind.DeviceInUse, ex);
                case 0x88890008: // AUDCLNT_E_UNSUPPORTED_FORMAT
                    return new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable, ex);
                case 0x88890004: // AUDCLNT_E_DEVICE_INVALIDATED
                    return new AudioRecorderException(AudioRecorderErrorKind.NoInput, ex);
            }
        }
        if (ex is UnauthorizedAccessException) return new AudioRecorderException(AudioRecorderErrorKind.AccessDenied, ex);
        return new AudioRecorderException(AudioRecorderErrorKind.FormatUnavailable, ex);
    }
}

/// <summary>Mischt N Kanäle per Mittelwert auf einen Kanal (NAudio bringt nur Stereo→Mono mit).</summary>
internal sealed class MonoMixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _buf = new float[0];

    public MonoMixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var need = count * _channels;
        if (_buf.Length < need) _buf = new float[need];
        var read = _source.Read(_buf, 0, need);
        var frames = read / _channels;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < _channels; c++) sum += _buf[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
