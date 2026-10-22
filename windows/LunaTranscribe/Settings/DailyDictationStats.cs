using System;

namespace LunaTranscribe.Settings;

/// <summary>
/// Tageszähler für die Übersicht: nur Datum und Zahlen, nie Text. Unabhängig vom Verlauf – zählt also
/// auch mit ausgeschaltetem Verlauf, übersteht Neustarts und beginnt jeden Tag bei null.
/// </summary>
public sealed record DailyDictationStats
{
    /// <summary>Kalendertag im Format yyyy-MM-dd (lokale Zeit).</summary>
    public string Day { get; init; } = "";
    public int Words { get; init; }
    public int Dictations { get; init; }

    /// <summary>Stand für <paramref name="date"/>: der gespeicherte Zähler, falls er von diesem Tag ist, sonst null.</summary>
    public DailyDictationStats On(DateTime? date = null)
    {
        var today = DayKey(date ?? DateTime.Now);
        return Day == today ? this : new DailyDictationStats { Day = today };
    }

    /// <summary>Zählt ein eingefügtes Diktat dazu (setzt an einem neuen Tag vorher zurück).</summary>
    public DailyDictationStats Adding(int words, DateTime? date = null)
    {
        var stats = On(date);
        return stats with { Words = stats.Words + words, Dictations = stats.Dictations + 1 };
    }

    public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd");

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
