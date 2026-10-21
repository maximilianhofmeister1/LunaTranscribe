using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LunaTranscribe.Engine;

namespace LunaTranscribe.Settings;

/// <summary>
/// Alle Sprachen, die mindestens eine Engine unterstützt.
/// Cohere: 14 Sprachen. Parakeet v3: 25 europäische Sprachen.
/// </summary>
public enum AppLanguage
{
    German, English, French, Italian, Spanish, Portuguese, Dutch, Polish, Greek,
    Arabic, Chinese, Japanese, Korean, Vietnamese,
    // Nur Parakeet v3
    Romanian, Danish, Swedish, Finnish, Hungarian, Estonian, Latvian, Lithuanian, Maltese,
    Czech, Slovak, Slovenian, Croatian, Bulgarian, Russian, Ukrainian,
}

public static class AppLanguageExtensions
{
    private static readonly Dictionary<AppLanguage, string> Codes = new()
    {
        [AppLanguage.German] = "de", [AppLanguage.English] = "en", [AppLanguage.French] = "fr",
        [AppLanguage.Italian] = "it", [AppLanguage.Spanish] = "es", [AppLanguage.Portuguese] = "pt",
        [AppLanguage.Dutch] = "nl", [AppLanguage.Polish] = "pl", [AppLanguage.Greek] = "el",
        [AppLanguage.Arabic] = "ar", [AppLanguage.Chinese] = "zh", [AppLanguage.Japanese] = "ja",
        [AppLanguage.Korean] = "ko", [AppLanguage.Vietnamese] = "vi",
        [AppLanguage.Romanian] = "ro", [AppLanguage.Danish] = "da", [AppLanguage.Swedish] = "sv",
        [AppLanguage.Finnish] = "fi", [AppLanguage.Hungarian] = "hu", [AppLanguage.Estonian] = "et",
        [AppLanguage.Latvian] = "lv", [AppLanguage.Lithuanian] = "lt", [AppLanguage.Maltese] = "mt",
        [AppLanguage.Czech] = "cs", [AppLanguage.Slovak] = "sk", [AppLanguage.Slovenian] = "sl",
        [AppLanguage.Croatian] = "hr", [AppLanguage.Bulgarian] = "bg", [AppLanguage.Russian] = "ru",
        [AppLanguage.Ukrainian] = "uk",
    };

    private static readonly HashSet<string> CohereCodes = new()
        { "en", "fr", "de", "it", "es", "pt", "el", "nl", "pl", "zh", "ja", "ko", "vi", "ar" };

    private static readonly HashSet<string> ParakeetCodes = new()
    {
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt",
        "mt", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "uk",
    };

    /// <summary>ISO-639-1-Code, z. B. „de“. Wird so in den Einstellungen gespeichert und an die Engine übergeben.</summary>
    public static string Code(this AppLanguage l) => Codes[l];

    public static AppLanguage? FromCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var kv in Codes)
            if (kv.Value == code) return kv.Key;
        return null;
    }

    /// <summary>Anzeigename in der Sprache des Systems (z. B. „Deutsch“, „Englisch“).</summary>
    public static string DisplayName(this AppLanguage l)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(l.Code()).DisplayName;
            // Windows liefert für Neutralkulturen teils „Deutsch (Deutschland)“-Varianten nicht, aber sicherheitshalber kürzen.
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name[..paren] : name;
        }
        catch
        {
            return l.Code();
        }
    }

    public static bool IsSupported(this AppLanguage l, EngineKind engine) => engine switch
    {
        EngineKind.Cohere => CohereCodes.Contains(l.Code()),
        EngineKind.Parakeet => ParakeetCodes.Contains(l.Code()),
        _ => false,
    };

    public static List<AppLanguage> Supported(EngineKind engine) =>
        Enum.GetValues<AppLanguage>()
            .Where(l => l.IsSupported(engine))
            .OrderBy(l => l.DisplayName(), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
