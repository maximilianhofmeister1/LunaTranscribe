using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LunaTranscribe.Audio;

/// <summary>
/// Lesen von Meeting-Aufnahmen (16 kHz, mono, PCM16 oder Float32). Ausschnitte werden direkt aus der
/// Datei gelesen, damit für die Transkription nie die ganze Aufnahme im RAM liegen muss.
/// </summary>
public static class WavFile
{
    public const int SampleRate = 16_000;

    /// <summary>true, wenn die Datei bereits im Meeting-Format (16 kHz, mono, PCM16/Float32) vorliegt.</summary>
    public static bool Is16kMono(string path)
    {
        try
        {
            using var reader = Open16kMono(path);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Dauer in Sekunden laut WAV-Header.</summary>
    public static double DurationSeconds(string path)
    {
        using var reader = new WaveFileReader(path);
        return reader.TotalTime.TotalSeconds;
    }

    /// <summary>Liest die komplette Aufnahme (16 kHz mono) – 1 h ≈ 230 MB. Nur für die Sprechererkennung.</summary>
    public static float[] ReadAll(string path)
    {
        using var reader = Open16kMono(path);
        var frames = reader.Length / reader.WaveFormat.BlockAlign;
        if (frames > int.MaxValue) throw new InvalidDataException("Aufnahme zu lang.");
        var samples = new float[frames];
        var sp = reader.ToSampleProvider();
        var total = 0;
        while (total < samples.Length)
        {
            var n = sp.Read(samples, total, Math.Min(1 << 16, samples.Length - total));
            if (n <= 0) break;
            total += n;
        }
        return total == samples.Length ? samples : samples[..total];
    }

    /// <summary>Liest den Ausschnitt [start, end) in Sekunden aus einer geöffneten 16-kHz-Mono-Datei.</summary>
    public static float[] ReadSlice(WaveFileReader reader, double start, double end)
    {
        var block = reader.WaveFormat.BlockAlign;
        var totalFrames = reader.Length / block;
        var first = Math.Clamp((long)(start * SampleRate), 0, totalFrames);
        var last = Math.Clamp((long)(end * SampleRate), first, totalFrames);
        var count = (int)(last - first);
        if (count <= 0) return Array.Empty<float>();

        reader.Position = first * block;
        var samples = new float[count];
        var sp = reader.ToSampleProvider();
        var total = 0;
        while (total < count)
        {
            var n = sp.Read(samples, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total == count ? samples : samples[..total];
    }

    /// <summary>Öffnet eine Aufnahme und prüft das Format (16 kHz, mono, PCM16/Float32).</summary>
    public static WaveFileReader Open16kMono(string path)
    {
        var reader = new WaveFileReader(path);
        var f = reader.WaveFormat;
        var ok = f.SampleRate == SampleRate && f.Channels == 1 &&
                 ((f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample == 16) ||
                  (f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32) ||
                  f.Encoding == WaveFormatEncoding.Extensible);
        if (ok) return reader;
        reader.Dispose();
        throw new InvalidDataException($"Unerwartetes Audioformat: {f}");
    }

    /// <summary>Wandelt eine beliebige WAV-Datei nach 16 kHz / mono / PCM16 (Selbsttests, Import).</summary>
    public static void ConvertTo16kMono(string source, string target)
    {
        using var reader = new WaveFileReader(source);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 1) sp = new MonoMixSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
        WaveFileWriter.CreateWaveFile16(target, sp);
    }
}
