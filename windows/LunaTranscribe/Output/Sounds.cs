using System;
using System.IO;
using System.Media;

namespace LunaTranscribe.Output;

/// <summary>
/// Kurze Start-/Stopp-Töne (Pendant zu „Pop“ und „Tink“ auf macOS). Werden beim Start einmalig
/// als WAV im Speicher erzeugt – keine Audiodateien im Bundle nötig.
/// </summary>
public static class Sounds
{
    private static readonly byte[] StartWav = Synth(startFreq: 620, endFreq: 880, ms: 90, volume: 0.35);
    private static readonly byte[] StopWav = Synth(startFreq: 1320, endFreq: 1180, ms: 70, volume: 0.30);

    public static void PlayStart() => Play(StartWav);
    public static void PlayStop() => Play(StopWav);

    private static void Play(byte[] wav)
    {
        try
        {
            using var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();   // asynchron, blockiert die UI nicht
        }
        catch { }
    }

    /// <summary>Sinus mit sanftem Frequenz-Sweep und Attack/Release-Hüllkurve, 44,1 kHz mono 16 bit.</summary>
    private static byte[] Synth(double startFreq, double endFreq, int ms, double volume)
    {
        const int rate = 44_100;
        var n = rate * ms / 1000;
        var data = new byte[n * 2];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / n;
            var freq = startFreq + (endFreq - startFreq) * t;
            phase += 2 * Math.PI * freq / rate;
            var attack = Math.Min(1, i / (rate * 0.005));
            var release = Math.Min(1, (n - i) / (rate * 0.03));
            var env = attack * release * (1 - 0.6 * t);
            var s = (short)(Math.Sin(phase) * env * volume * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)(s >> 8);
        }

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return ms2.ToArray();
    }
}
