using System;
using System.Globalization;
using System.Text;

namespace LunaCore;

/// <summary>
/// FNV-1a (64 Bit) über Bytes. Dient als Revision (optimistische Sperre) und als Änderungsmerkmal für den
/// Suchindex – nicht als Sicherheitsmerkmal. Gleiche Bytes ergeben dieselbe Revision wie in der macOS-Version.
/// </summary>
public static class ContentHash
{
    /// <summary>16 Hex-Ziffern, klein.</summary>
    public static string Hex(ReadOnlySpan<byte> data)
    {
        ulong hash = 0xcbf29ce484222325;
        foreach (var b in data)
        {
            hash ^= b;
            hash = unchecked(hash * 0x00000100000001b3);
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static string Hex(byte[] data) => Hex(new ReadOnlySpan<byte>(data));

    public static string Hex(string text) => Hex(Encoding.UTF8.GetBytes(text));
}
