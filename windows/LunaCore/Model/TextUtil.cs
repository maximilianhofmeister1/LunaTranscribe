using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LunaCore;

/// <summary>Kleine Helfer, die Swifts String-Semantik nachbilden (Zeichen = Graphem, <c>.whitespaces</c> ohne Zeilenumbrüche).</summary>
internal static class TextUtil
{
    /// <summary>Wie Swifts <c>CharacterSet.whitespaces</c>: Tab und Leerzeichen-Kategorie Zs, keine Zeilenumbrüche.</summary>
    public static bool IsHorizontalSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary><c>trimmingCharacters(in: .whitespaces)</c></summary>
    public static string TrimSpaces(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsHorizontalSpace(s[start])) start++;
        while (end > start && IsHorizontalSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Graphem-Cluster („Characters“ in Swift).</summary>
    public static List<string> Characters(string s)
    {
        var result = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) result.Add((string)e.Current);
        return result;
    }

    /// <summary>Länge in Graphemen wie Swifts <c>String.count</c>.</summary>
    public static int Length(string s) => new StringInfo(s).LengthInTextElements;

    /// <summary>Die ersten <paramref name="max"/> Grapheme.</summary>
    public static string Prefix(string s, int max)
    {
        var info = new StringInfo(s);
        return info.LengthInTextElements <= max ? s : info.SubstringByTextElements(0, max);
    }

    /// <summary>Kategorie des ersten Unicode-Skalars eines Graphems (für <c>isLetter</c>/<c>isNumber</c>).</summary>
    public static UnicodeCategory Category(string grapheme) =>
        Rune.TryGetRuneAt(grapheme, 0, out var rune) ? Rune.GetUnicodeCategory(rune) : UnicodeCategory.OtherNotAssigned;

    public static bool IsLetter(UnicodeCategory c) => c is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
        or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    public static bool IsNumber(UnicodeCategory c) =>
        c is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    public static bool IsMark(UnicodeCategory c) =>
        c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    public static bool IsPunctuationOrSymbol(UnicodeCategory c) => c is UnicodeCategory.ConnectorPunctuation
        or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
        or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
        or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    /// <summary>Teilt an Leerraum (inkl. Zeilenumbrüchen), ohne leere Teile – wie <c>split(whereSeparator: \.isWhitespace)</c>.</summary>
    public static List<string> SplitWhitespace(string s)
    {
        var result = new List<string>();
        int start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                if (start >= 0) { result.Add(s[start..i]); start = -1; }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) result.Add(s[start..]);
        return result;
    }
}
