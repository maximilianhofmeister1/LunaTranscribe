using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>
/// Über welche lokal installierte CLI eine Meeting-Zusammenfassung entsteht. Alle drei laufen mit dem Konto bzw. Abo des
/// Nutzers (kein API-Schlüssel), schicken den Text aber an den jeweiligen Anbieter.
///
/// Rohwerte (<see cref="SummaryProviderExtensions.RawValue"/>) sind gespeicherte Daten (Einstellung <c>SummaryProvider</c>,
/// Feld <c>provider</c> in <c>meeting.json</c>, gleich wie in der macOS-Version) – nie ändern. Unbekannte Werte (z. B. aus
/// einer neueren Version) gelten beim Lesen als „nicht gesetzt“.
/// </summary>
public enum SummaryProvider
{
    Claude,
    Antigravity,
    Codex,
}

public static class SummaryProviderExtensions
{
    public static readonly SummaryProvider[] All = Enum.GetValues<SummaryProvider>();

    public static string RawValue(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "antigravity",
        SummaryProvider.Codex => "codex",
        _ => "claude",
    };

    /// <summary>Rohwert → Anbieter; null für fehlende oder unbekannte Werte.</summary>
    public static SummaryProvider? FromRawValue(string? raw) => raw switch
    {
        "claude" => SummaryProvider.Claude,
        "antigravity" => SummaryProvider.Antigravity,
        "codex" => SummaryProvider.Codex,
        _ => null,
    };

    /// <summary>Kurzer Name für Auswahl und „Erstellt mit …“.</summary>
    public static string Label(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Gemini",
        SummaryProvider.Codex => "ChatGPT",
        _ => "Claude",
    };

    /// <summary>Name des Kommandozeilenprogramms, wie ihn Hinweise und Fehlermeldungen nennen.</summary>
    public static string CliName(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Antigravity CLI",
        SummaryProvider.Codex => "Codex CLI",
        _ => "Claude CLI",
    };

    /// <summary>Befehl im <c>PATH</c> (ohne Endung).</summary>
    public static string Command(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "agy",
        SummaryProvider.Codex => "codex",
        _ => "claude",
    };

    /// <summary>Wer den Text verarbeitet – für den Cloud-Hinweis.</summary>
    public static string Vendor(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Google",
        SummaryProvider.Codex => "OpenAI",
        _ => "Anthropic",
    };

    /// <summary>Das Konto, mit dem die CLI angemeldet ist.</summary>
    public static string Account(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Google-Konto",
        SummaryProvider.Codex => "ChatGPT-Konto",
        _ => "Claude-Konto",
    };
}

/// <summary>
/// Liest <c>provider</c> tolerant: Ein unbekannter Anbieter aus einer neueren Version darf das ganze Meeting nicht
/// unlesbar machen und gilt als null.
/// </summary>
public sealed class OptionalSummaryProviderConverter : JsonConverter<SummaryProvider?>
{
    public override bool HandleNull => true;

    public override SummaryProvider? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return SummaryProviderExtensions.FromRawValue(reader.GetString());
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, SummaryProvider? value, JsonSerializerOptions options)
    {
        if (value is { } p) writer.WriteStringValue(p.RawValue());
        else writer.WriteNullValue();
    }
}
