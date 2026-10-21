using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LunaTranscribe.UI.Theme;

/// <summary>
/// Design-System „Mondnacht“ (wie UI/Theme/Theme.swift der macOS-Version): warm und verspielt.
/// Hell = Dämmerung (cremiges Mondlicht), dunkel = Mondnacht (Nachtblau). Alle Views nutzen nur diese
/// Farben – als DynamicResource „Luna.*“, damit ein Wechsel des Windows-Farbmodus sofort greift.
/// </summary>
public static class LunaTheme
{
    private sealed record Token(string Key, uint Light, uint Dark, double LightAlpha = 1, double DarkAlpha = 1);

    private static readonly Token[] Tokens =
    {
        new("Luna.Background", 0xFBF6EC, 0x1A1830),     // Fensterhintergrund
        new("Luna.Surface", 0xFFFDF8, 0x26223F),        // Karten und Eingabeflächen
        new("Luna.SurfaceMuted", 0xF3ECDD, 0x211D38),   // Seitenleiste, zurückgenommene Flächen
        new("Luna.Ink", 0x2B2540, 0xEDE6D6),            // Haupttext
        new("Luna.InkMuted", 0x6E6682, 0xA9A1BD),       // Nebentext
        new("Luna.Lavender", 0x7E6BB8, 0xB9A7F0),       // Markenakzent
        new("Luna.Honey", 0xD9962A, 0xE8B04B),          // Aufnahme, Hervorhebungen
        new("Luna.Sage", 0x5E9480, 0x8FB9A8),           // bereit, erfolgreich
        new("Luna.Rose", 0xC0505D, 0xE68A93),           // Fehler, Löschen
        new("Luna.Stroke", 0x2B2540, 0xEDE6D6, 0.10, 0.12),   // Haarlinien um Karten
        new("Luna.OnAccent", 0xFFFDF8, 0x1A1830),       // Text auf Lavendel-Flächen
        // Abgeleitete Flächen (SwiftUI: color.opacity(x))
        new("Luna.LavenderSoft", 0x7E6BB8, 0xB9A7F0, 0.13, 0.16),
        new("Luna.HoneySoft", 0xD9962A, 0xE8B04B, 0.13, 0.16),
        new("Luna.SageSoft", 0x5E9480, 0x8FB9A8, 0.13, 0.16),
        new("Luna.RoseSoft", 0xC0505D, 0xE68A93, 0.12, 0.14),
        new("Luna.InkMutedSoft", 0x6E6682, 0xA9A1BD, 0.13, 0.16),
        new("Luna.SurfaceHover", 0xFFFDF8, 0x26223F, 0.5, 0.5),
        new("Luna.SurfaceMutedHover", 0xF3ECDD, 0x211D38, 0.6, 0.6),
        new("Luna.Shadow", 0x000000, 0x000000, 0.04, 0.25),
    };

    /// <summary>Nachthimmel für Overlay und Hero-Flächen – in beiden Modi dunkel.</summary>
    public static readonly Color NightTop = Rgb(0x3A2F6B);
    public static readonly Color NightBottom = Rgb(0x15132B);
    public static readonly Color Moonlight = Rgb(0xFBF1DC);
    public static readonly Color HoneyNight = Rgb(0xE8B04B);
    public static readonly Color LavenderNight = Rgb(0xB9A7F0);

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    /// <summary>Setzt die Farben passend zum Windows-Farbmodus und folgt späteren Wechseln.</summary>
    public static void Install(ResourceDictionary resources)
    {
        Apply(resources, SystemUsesDarkMode());
        resources["Luna.NightVertical"] = Freeze(new LinearGradientBrush(NightTop, NightBottom, 90));
        resources["Luna.NightDiagonal"] = Freeze(new LinearGradientBrush(NightTop, NightBottom, new Point(0, 0), new Point(1, 1)));
        resources["Luna.Moonlight"] = Freeze(new SolidColorBrush(Moonlight));
        resources["Luna.MoonlightMuted"] = Freeze(new SolidColorBrush(Color.FromArgb(0xBF, Moonlight.R, Moonlight.G, Moonlight.B)));
        resources["Luna.HoneyNight"] = Freeze(new SolidColorBrush(HoneyNight));
        resources["Luna.LavenderNight"] = Freeze(new SolidColorBrush(LavenderNight));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var dark = SystemUsesDarkMode();
            if (dark == IsDark) return;
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(resources, dark));
        };
    }

    public static void Apply(ResourceDictionary resources, bool dark)
    {
        IsDark = dark;
        foreach (var t in Tokens)
        {
            var hex = dark ? t.Dark : t.Light;
            var alpha = dark ? t.DarkAlpha : t.LightAlpha;
            var c = Rgb(hex);
            c.A = (byte)Math.Round(alpha * 255);
            resources[t.Key] = Freeze(new SolidColorBrush(c));
            resources[t.Key + ".Color"] = c;
        }
        Changed?.Invoke();
    }

    /// <summary>Feste, freundliche Farben für Sprecher (Reihenfolge des ersten Auftretens).</summary>
    public static Brush SpeakerBrush(int index)
    {
        string[] keys = { "Luna.Lavender", "Luna.Honey", "Luna.Sage", "Luna.Rose" };
        var i = index % 6;
        if (i < keys.Length) return (Brush)Application.Current.Resources[keys[i]];
        return Freeze(new SolidColorBrush(Rgb(i == 4 ? 0x6FA8DC : 0xC27BA0u)));
    }

    /// <summary>Windows-Einstellung „App-Modus“ (Einstellungen → Personalisierung → Farben).</summary>
    public static bool SystemUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    public static Color Rgb(uint hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
