using System;
using System.Globalization;
using System.Text;

namespace LunaCore;

/// <summary>Einheitliche Normalisierung für Suche und Vergleiche: klein, ohne Akzente/Umlaut-Punkte.</summary>
public static class TextFolding
{
    /// <summary>
    /// Wie der FTS5-Tokenizer (<c>unicode61 remove_diacritics 2</c>): Akzente weg, klein – „ß“ bleibt „ß“
    /// (eine Groß-/Kleinschreibungs-Faltung würde „ss“ daraus machen und im Index nichts mehr finden).
    /// </summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>Enthält <paramref name="text"/> den gefalteten Suchbegriff (bereits gefaltet übergeben)?</summary>
    public static bool Contains(string text, string foldedNeedle) =>
        Fold(text).Contains(foldedNeedle, StringComparison.Ordinal);
}
