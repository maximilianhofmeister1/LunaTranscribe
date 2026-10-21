using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

/// <summary>
/// Alle Sprachen, die mindestens eine Engine unterstützt.
/// Cohere: 14 Sprachen. Parakeet v3: 25 europäische Sprachen.
/// Gespeichert (Einstellungen, meeting.json, job.json) wird immer der ISO-Code – wie in der macOS-Version.
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
    private static readonly Dictionary<AppLanguage, (string Code, string Name)> Info = new()
    {
        [AppLanguage.German] = ("de", "Deutsch"), [AppLanguage.English] = ("en", "Englisch"),
        [AppLanguage.French] = ("fr", "Französisch"), [AppLanguage.Italian] = ("it", "Italienisch"),
        [AppLanguage.Spanish] = ("es", "Spanisch"), [AppLanguage.Portuguese] = ("pt", "Portugiesisch"),
        [AppLanguage.Dutch] = ("nl", "Niederländisch"), [AppLanguage.Polish] = ("pl", "Polnisch"),
        [AppLanguage.Greek] = ("el", "Griechisch"), [AppLanguage.Arabic] = ("ar", "Arabisch"),
        [AppLanguage.Chinese] = ("zh", "Chinesisch"), [AppLanguage.Japanese] = ("ja", "Japanisch"),
        [AppLanguage.Korean] = ("ko", "Koreanisch"), [AppLanguage.Vietnamese] = ("vi", "Vietnamesisch"),
        [AppLanguage.Romanian] = ("ro", "Rumänisch"), [AppLanguage.Danish] = ("da", "Dänisch"),
        [AppLanguage.Swedish] = ("sv", "Schwedisch"), [AppLanguage.Finnish] = ("fi", "Finnisch"),
        [AppLanguage.Hungarian] = ("hu", "Ungarisch"), [AppLanguage.Estonian] = ("et", "Estnisch"),
        [AppLanguage.Latvian] = ("lv", "Lettisch"), [AppLanguage.Lithuanian] = ("lt", "Litauisch"),
        [AppLanguage.Maltese] = ("mt", "Maltesisch"), [AppLanguage.Czech] = ("cs", "Tschechisch"),
        [AppLanguage.Slovak] = ("sk", "Slowakisch"), [AppLanguage.Slovenian] = ("sl", "Slowenisch"),
        [AppLanguage.Croatian] = ("hr", "Kroatisch"), [AppLanguage.Bulgarian] = ("bg", "Bulgarisch"),
        [AppLanguage.Russian] = ("ru", "Russisch"), [AppLanguage.Ukrainian] = ("uk", "Ukrainisch"),
    };

    private static readonly HashSet<string> CohereCodes = new()
        { "en", "fr", "de", "it", "es", "pt", "el", "nl", "pl", "zh", "ja", "ko", "vi", "ar" };

    private static readonly HashSet<string> ParakeetCodes = new()
    {
        "bg", "hr", "cs", "da", "nl", "en", "et", "fi", "fr", "de", "el", "hu", "it", "lv", "lt",
        "mt", "pl", "pt", "ro", "ru", "sk", "sl", "es", "sv", "uk",
    };

    /// <summary>ISO-639-1-Code, z. B. „de“. Wird so gespeichert und an die Engine übergeben.</summary>
    public static string Code(this AppLanguage l) => Info[l].Code;

    public static AppLanguage? FromCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var kv in Info)
            if (kv.Value.Code == code) return kv.Key;
        return null;
    }

    /// <summary>Deutscher Name („Deutsch“, „Englisch“) – die Oberfläche ist deutsch, unabhängig von der Systemsprache.</summary>
    public static string DisplayName(this AppLanguage l) => Info[l].Name;

    public static bool IsSupported(this AppLanguage l, EngineKind engine) => engine switch
    {
        EngineKind.Cohere => CohereCodes.Contains(l.Code()),
        EngineKind.Parakeet => ParakeetCodes.Contains(l.Code()),
        _ => false,
    };

    public static List<AppLanguage> Supported(EngineKind engine) =>
        Enum.GetValues<AppLanguage>()
            .Where(l => l.IsSupported(engine))
            .OrderBy(l => l.DisplayName(), StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), true))
            .ToList();
}
