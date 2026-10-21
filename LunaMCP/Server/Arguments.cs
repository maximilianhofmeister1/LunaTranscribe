using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace LunaMCP.Server;

/// <summary>Typisierter Zugriff auf Tool- und Prompt-Argumente mit Fehlermeldungen, die Claude weiterhelfen.</summary>
internal sealed class Arguments
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;
    private readonly TimeZoneInfo _timeZone;

    public Arguments(IDictionary<string, JsonElement>? values, TimeZoneInfo timeZone)
    {
        _values = values is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(values);
        _timeZone = timeZone;
    }

    public Arguments(JsonElement objectValue, TimeZoneInfo timeZone)
        : this(objectValue.ValueKind == JsonValueKind.Object
                   ? objectValue.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
                   : null, timeZone) { }

    private JsonElement? Value(string key) =>
        _values.TryGetValue(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

    public bool Has(string key) => _values.ContainsKey(key);

    public string? String(string key)
    {
        if (Value(key) is not { } value) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var trimmed = value.GetString()!.Trim();
                return trimmed.Length == 0 ? null : trimmed;
            case JsonValueKind.Number when value.TryGetInt64(out var i):
                return i.ToString(CultureInfo.InvariantCulture);
            default:
                throw new ToolError($"„{key}“ muss ein Text sein.");
        }
    }

    public string RequiredString(string key) => String(key) ?? throw new ToolError($"„{key}“ fehlt.");

    /// <summary>Text, der leer sein darf (z. B. Sprechername zurücksetzen). Fehlt der Schlüssel ganz, ist das ein Fehler.</summary>
    public string PresentString(string key)
    {
        if (!_values.ContainsKey(key)) throw new ToolError($"„{key}“ fehlt.");
        return String(key) ?? "";
    }

    public int Int(string key, int fallback, int minimum, int maximum)
    {
        if (Value(key) is not { } value) return fallback;
        long number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var i):
                number = i;
                break;
            case JsonValueKind.Number when value.TryGetDouble(out var d) && d == Math.Round(d):
                number = (long)d;
                break;
            case JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s):
                number = s;
                break;
            default:
                throw new ToolError($"„{key}“ muss eine ganze Zahl sein.");
        }
        if (number < minimum || number > maximum)
            throw new ToolError($"„{key}“ muss zwischen {minimum} und {maximum} liegen.");
        return (int)number;
    }

    public bool? Bool(string key)
    {
        if (Value(key) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when value.GetString() == "true" => true,
            JsonValueKind.String when value.GetString() == "false" => false,
            _ => throw new ToolError($"„{key}“ muss true oder false sein."),
        };
    }

    public List<string>? StringArray(string key)
    {
        if (Value(key) is not { } value) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new ToolError($"„{key}“ muss eine Liste von Texten sein.")).ToList(),
            JsonValueKind.String => new List<string> { value.GetString()! },
            _ => throw new ToolError($"„{key}“ muss eine Liste von Texten sein."),
        };
    }

    public List<JsonElement>? ObjectArray(string key)
    {
        if (Value(key) is not { } value) return null;
        if (value.ValueKind != JsonValueKind.Array) throw new ToolError($"„{key}“ muss eine Liste von Objekten sein.");
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Object
            ? item
            : throw new ToolError($"„{key}“ muss eine Liste von Objekten sein.")).ToList();
    }

    /// <summary>Tagesbeginn eines Datums „JJJJ-MM-TT“ in der Zeitzone des Nutzers (oder ein voller ISO-Zeitpunkt), als UTC.</summary>
    public DateTime? DayStart(string key)
    {
        if (String(key) is not { } text) return null;
        return ParseDate(text, _timeZone)
               ?? throw new ToolError($"„{key}“ muss ein Datum im Format JJJJ-MM-TT sein, z. B. 2026-09-25.");
    }

    /// <summary>Ende eines Tages (exklusiv): „to: 2026-09-25“ schließt den ganzen 25. ein.</summary>
    public DateTime? DayEnd(string key)
    {
        if (String(key) is not { } text) return null;
        if (text.Length == 10 && DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), _timeZone);
        return ParseDate(text, _timeZone)
               ?? throw new ToolError($"„{key}“ muss ein Datum im Format JJJJ-MM-TT sein, z. B. 2026-09-25.");
    }

    /// <summary>Zeitmarke „mm:ss“, „h:mm:ss“ oder Sekunden.</summary>
    public double? Time(string key)
    {
        if (Value(key) is not { } value) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.GetDouble();
            case JsonValueKind.String:
                var s = value.GetString()!.Trim();
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
                var raw = s.Split(':');
                var parts = raw.Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null).ToList();
                if (raw.Length is < 2 or > 3 || parts.Any(p => p is null))
                    throw new ToolError($"„{key}“ muss eine Zeitmarke wie 12:04 oder 1:02:30 sein.");
                return parts.Aggregate(0, (total, p) => total * 60 + p!.Value);
            default:
                throw new ToolError($"„{key}“ muss eine Zeitmarke wie 12:04 sein.");
        }
    }

    public static DateTime? ParseDate(string text, TimeZoneInfo timeZone)
    {
        if (text.Length == 10)
        {
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), timeZone);
        }
        if (!text.Contains('T')) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }
}
