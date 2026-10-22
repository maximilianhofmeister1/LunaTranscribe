using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LunaCore;

/// <summary>
/// Rechnet Texte in Vektoren um (inhaltliche Suche). Die echte Implementierung (ONNX Runtime, CPU) steckt in
/// luna-mcp; der Kern kennt nur diese Schnittstelle – Tests nutzen einen einfachen Ersatz.
/// Viele Modelle unterscheiden Anfrage und Dokument (E5: „query: “ / „passage: “); darum zwei Methoden.
/// Ergebnisse sollen L2-normalisiert sein; der Index normalisiert zur Sicherheit noch einmal.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Eindeutiger Modellname; wechselt er, werden alle Vektoren neu berechnet.</summary>
    string ModelID { get; }
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
}

public static class VectorMath
{
    public static float[] Normalized(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector) sum += v * v;
        var norm = (float)Math.Sqrt(sum);
        if (norm <= 0) return vector;
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = vector[i] / norm;
        return result;
    }

    public static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var norm = Math.Sqrt(na * nb);
        return norm > 0 ? (float)(dot / norm) : 0;
    }

    /// <summary>Float32, Little Endian – wie in der macOS-Version.</summary>
    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    /// <summary>Kopiert statt umzudeuten: Blobs aus SQLite sind nicht zwingend für <c>float</c> ausgerichtet.</summary>
    public static float[] FromBytes(byte[] data)
    {
        var result = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, result, 0, result.Length * sizeof(float));
        return result;
    }
}
