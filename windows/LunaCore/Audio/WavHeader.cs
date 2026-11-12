using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LunaCore;

/// <summary>
/// Lesen, Prüfen und Reparieren von WAV-Köpfen (RIFF). Reine Byte-Arbeit ohne NAudio, damit die Reparatur in
/// LunaCore getestet werden kann (Port von <c>WAVFile.swift</c>).
///
/// Hintergrund: Ein WAV-Kopf enthält die Größe der Audiodaten. Bricht die Aufnahme hart ab (Absturz, Task beenden,
/// Strom weg), steht dort 0 oder ein veralteter Wert, obwohl die Samples auf der Platte liegen.
/// <see cref="Repair"/> rechnet die Größen aus der Dateigröße neu aus.
/// </summary>
public static class WavHeader
{
    public sealed class FormatException : Exception
    {
        public FormatException(string message) : base(message) { }
    }

    public sealed record Info(int SampleRate, int Channels, int BlockAlign, long DataOffset, long DeclaredDataBytes, long AvailableDataBytes)
    {
        /// <summary>Vollständige Frames ab <see cref="DataOffset"/> bis Dateiende.</summary>
        public long Frames => AvailableDataBytes / Math.Max(BlockAlign, 1);
        public double DurationSeconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;
        /// <summary>Kopf passt zur Datei (nach <see cref="Repair"/> immer true).</summary>
        public bool IsConsistent => DeclaredDataBytes == AvailableDataBytes;
    }

    /// <summary>Größe des kanonischen Kopfes (RIFF + fmt(16) + data), wie ihn NAudio für PCM schreibt.</summary>
    public const int CanonicalHeaderSize = 44;

    /// <summary>Kanonischer 44-Byte-Kopf für PCM16 (little endian) – so nimmt die Windows-Version Meetings auf.</summary>
    public static byte[] Pcm16Header(int sampleRate, int channels, long dataBytes)
    {
        var blockAlign = channels * 2;
        var header = new byte[CanonicalHeaderSize];
        var span = header.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Clamp(dataBytes + CanonicalHeaderSize - 8));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(sampleRate * blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], Clamp(dataBytes));
        return header;
    }

    /// <summary>
    /// Liest den Kopf. Der <c>data</c>-Abschnitt gilt als letzter Abschnitt der Datei – so schreiben alle Aufnahme-Writer
    /// (NAudio, Apple), und nur dann ist eine Reparatur eindeutig.
    /// </summary>
    public static Info Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Köpfe sind klein; auch mit fact-/JUNK-/LIST-Abschnitten weit unter 64 KB.
        var head = new byte[(int)Math.Min(stream.Length, 64 * 1024)];
        stream.ReadExactly(head);
        return Parse(head, stream.Length);
    }

    /// <summary>
    /// Repariert den Kopf einer abgebrochenen Aufnahme: RIFF- und data-Größe aus der Dateigröße, ein angefangenes letztes
    /// Frame wird abgeschnitten. Unveränderte Dateien bleiben unberührt.
    /// </summary>
    public static Info Repair(string path)
    {
        var info = Inspect(path);
        var end = info.DataOffset + info.AvailableDataBytes;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (info.IsConsistent && stream.Length == end) return info;

        if (stream.Length > end) stream.SetLength(end);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, Clamp(end - 8));
        stream.Position = 4;
        stream.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, Clamp(info.AvailableDataBytes));
        stream.Position = info.DataOffset - 4;
        stream.Write(value);
        stream.Flush(flushToDisk: true);
        return info with { DeclaredDataBytes = info.AvailableDataBytes };
    }

    internal static Info Parse(ReadOnlySpan<byte> bytes, long fileSize)
    {
        if (bytes.Length < 12 || Ascii(bytes, 0) != "RIFF" || Ascii(bytes, 8) != "WAVE") throw new FormatException("Keine WAV-Datei.");

        (int Rate, int Channels, int BlockAlign)? format = null;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = Ascii(bytes, offset);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            var payload = offset + 8;
            if (id == "data")
            {
                if (format is not { } f) throw new FormatException("WAV-Datei ohne „fmt “-Abschnitt.");
                var available = Math.Max(0, fileSize - payload) / f.BlockAlign * f.BlockAlign;
                return new Info(f.Rate, f.Channels, f.BlockAlign, payload, size, available);
            }
            if (id == "fmt ")
            {
                if (payload + 16 > bytes.Length) throw new FormatException("WAV-Datei ohne „fmt “-Abschnitt.");
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(payload + 2)..]);
                var rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(payload + 4)..]);
                int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(payload + 12)..]);
                if (channels <= 0 || rate <= 0 || blockAlign <= 0)
                    throw new FormatException($"WAV-Format nicht unterstützt: {channels} Kanäle, {rate} Hz, Block {blockAlign}");
                format = (rate, channels, blockAlign);
            }
            var next = payload + (long)size + (size & 1);   // Abschnitte sind auf gerade Längen aufgefüllt
            if (next > int.MaxValue) break;
            offset = (int)next;
        }
        throw new FormatException("WAV-Datei ohne „data“-Abschnitt.");
    }

    private static string Ascii(ReadOnlySpan<byte> bytes, int at) => Encoding.ASCII.GetString(bytes.Slice(at, 4));

    /// <summary>RIFF-Größen sind 32 Bit – bei 16 kHz mono PCM16 reicht das für gut 37 Stunden.</summary>
    private static uint Clamp(long value) => (uint)Math.Clamp(value, 0, uint.MaxValue);
}
