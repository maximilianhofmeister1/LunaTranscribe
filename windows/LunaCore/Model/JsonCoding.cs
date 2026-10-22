using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>
/// Einheitliches JSON für alle Dateien, die App, luna-mcp und die macOS-Version teilen (meeting.json, job.json,
/// Aktivitätsprotokoll). Ausgabe wie Swifts <c>JSONEncoder</c> mit <c>[.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]</c>
/// und <c>.iso8601</c>: Schlüssel sortiert, „"key" : value“, zwei Leerzeichen Einzug, Daten als
/// <c>2026-09-25T14:03:00Z</c> (UTC, ohne Sekundenbruchteile), UUIDs groß, Umlaute und „/“ unmaskiert.
/// So kann ein Meeting-Ordner (z. B. in einem Cloud-Laufwerk) zwischen Mac und PC geteilt werden.
/// </summary>
public static class JsonCoding
{
    /// <summary>Optionen für Modelltypen mit <c>[JsonPropertyName]</c>; fehlende optionale Werte werden weggelassen.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new Iso8601DateConverter());
        o.Converters.Add(new UpperGuidConverter());
        o.Converters.Add(new AppLanguageConverter());
        o.Converters.Add(new EngineKindConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>Kodiert einen Wert im Swift-kompatiblen Format (mit <paramref name="pretty"/> = false einzeilig, für JSON Lines).</summary>
    public static string Encode<T>(T value, bool pretty = true)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        var sb = new StringBuilder();
        Write(sb, node, pretty, 0);
        return sb.ToString();
    }

    /// <summary>Wie <see cref="Encode{T}"/>, als UTF-8 ohne BOM.</summary>
    public static byte[] EncodeToUtf8<T>(T value, bool pretty = true) => Encoding.UTF8.GetBytes(Encode(value, pretty));

    /// <summary>Dekodiert; wirft <see cref="JsonException"/> bei ungültigem Inhalt oder fehlenden Pflichtfeldern.</summary>
    public static T Decode<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Leeres JSON-Dokument.");

    public static T Decode<T>(byte[] utf8) => Decode<T>(new ReadOnlySpan<byte>(utf8));

    public static T Decode<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new JsonException("Leeres JSON-Dokument.");

    // MARK: Datum

    /// <summary>„2026-09-25T14:03:00Z“ – UTC, Sekunden abgeschnitten. <c>Unspecified</c> gilt als UTC.</summary>
    public static string FormatDate(DateTime date)
    {
        var utc = ToUtc(date);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Liest ISO-8601 (mit „Z“ oder Offset, Sekundenbruchteile werden toleriert) als UTC-Zeitpunkt.</summary>
    public static DateTime ParseDate(string text)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed) || !text.Contains('T'))
            throw new JsonException($"Ungültiges Datum: {text}");
        return parsed.UtcDateTime;
    }

    /// <summary>Zeitpunkt in UTC; <c>Unspecified</c> wird als UTC verstanden (so liefert ihn auch das Dekodieren).</summary>
    public static DateTime ToUtc(DateTime date) => date.Kind switch
    {
        DateTimeKind.Utc => date,
        DateTimeKind.Local => date.ToUniversalTime(),
        _ => DateTime.SpecifyKind(date, DateTimeKind.Utc),
    };

    /// <summary>Ortszeit für Anzeige, Titel und Ordnernamen (wie <c>DateFormatter</c> mit der Systemzeitzone).</summary>
    public static DateTime ToLocal(DateTime date) => ToUtc(date).ToLocalTime();

    // MARK: Swift-kompatible Ausgabe

    private static void Write(StringBuilder sb, JsonNode? node, bool pretty, int depth)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                var props = obj.OrderBy(p => p.Key, SwiftKeyComparer.Instance).ToList();
                if (props.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, pretty, depth + 1);
                    WriteString(sb, props[i].Key);
                    sb.Append(pretty ? " : " : ":");
                    Write(sb, props[i].Value, pretty, depth + 1);
                }
                NewLine(sb, pretty, depth);
                sb.Append('}');
                break;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    NewLine(sb, pretty, depth + 1);
                    Write(sb, arr[i], pretty, depth + 1);
                }
                NewLine(sb, pretty, depth);
                sb.Append(']');
                break;
            default:
                var value = node.AsValue();
                if (value.GetValueKind() == JsonValueKind.String) WriteString(sb, value.GetValue<string>());
                else sb.Append(value.ToJsonString());
                break;
        }
    }

    private static void NewLine(StringBuilder sb, bool pretty, int depth)
    {
        if (!pretty) return;
        sb.Append('\n').Append(' ', depth * 2);
    }

    /// <summary>Maskiert nur, was JSON verlangt (Anführungszeichen, Backslash, Steuerzeichen) – wie Swift ohne Slash-Maskierung.</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Reihenfolge wie Foundations <c>.sortedKeys</c>: ohne Beachtung der Groß-/Kleinschreibung, Ziffernfolgen
    /// numerisch („Sprecher 2“ vor „Sprecher 10“), bei Gleichstand ordinal.
    /// </summary>
    private sealed class SwiftKeyComparer : IComparer<string>
    {
        public static readonly SwiftKeyComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return string.CompareOrdinal(x, y);
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int cmp = string.CompareOrdinal(a, b);
                    if (cmp != 0) return cmp;
                    continue;
                }
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
            int rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }

    // MARK: Konverter

    private sealed class Iso8601DateConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? ParseDate(reader.GetString()!)
                : throw new JsonException("Datum muss ein ISO-8601-Text sein.");

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }

    /// <summary>UUIDs groß wie Swifts <c>uuidString</c>; gelesen wird jede Schreibweise.</summary>
    private sealed class UpperGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g)
                ? g
                : throw new JsonException("Ungültige UUID.");

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
    }

    private sealed class AppLanguageConverter : JsonConverter<AppLanguage>
    {
        public override AppLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var code = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return AppLanguageExtensions.FromCode(code) ?? throw new JsonException($"Unbekannte Sprache: {code}");
        }

        public override void Write(Utf8JsonWriter writer, AppLanguage value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Code());
    }

    private sealed class EngineKindConverter : JsonConverter<EngineKind>
    {
        public override EngineKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var key = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return key switch
            {
                "parakeet" => EngineKind.Parakeet,
                "cohere" => EngineKind.Cohere,
                _ => throw new JsonException($"Unbekannte Engine: {key}"),
            };
        }

        public override void Write(Utf8JsonWriter writer, EngineKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Key());
    }
}
