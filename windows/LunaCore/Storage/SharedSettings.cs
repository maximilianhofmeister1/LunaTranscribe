using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LunaCore;

/// <summary>
/// Die Einstellungen, die luna-mcp aus der Einstellungsdatei der App (<c>%APPDATA%\LunaTranscribe\settings.json</c>)
/// liest. Geschrieben werden sie nur von der App (<c>SettingsStore</c>, System.Text.Json mit PascalCase-Namen);
/// die Schlüssel in <see cref="Key"/> sind gespeicherte Daten und müssen mit den Eigenschaften dort übereinstimmen –
/// nie umbenennen. Fehlende Datei oder fehlende Schlüssel ergeben die Standardwerte.
/// </summary>
public sealed record SharedSettings
{
    public static class Key
    {
        /// <summary>string, "" = Standardordner.</summary>
        public const string MeetingFolder = "MeetingFolder";
        /// <summary>bool, Standard true.</summary>
        public const string VocabularyEnabled = "VocabularyEnabled";
        /// <summary>Array aus <c>{ "Id", "Term", "Aliases" }</c>.</summary>
        public const string VocabularyEntries = "VocabularyEntries";
        /// <summary>string, fehlend oder "" = <see cref="SummaryPrompt.DefaultTemplate"/>.</summary>
        public const string SummaryPrompt = "SummaryPrompt";
        /// <summary>string: „meeting“ oder Sprachcode; fehlend/ungültig = wie das Meeting.</summary>
        public const string SummaryLanguage = "SummaryLanguage";
        public const string McpAccessEnabled = "McpAccessEnabled";
        public const string McpWriteEnabled = "McpWriteEnabled";
        public const string McpConfirmRenames = "McpConfirmRenames";
        public const string SemanticSearchEnabled = "SemanticSearchEnabled";
    }

    /// <summary>%APPDATA%\LunaTranscribe\settings.json</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LunaTranscribe", "settings.json");

    /// <summary>Pfad aus den Einstellungen; "" = Standardordner.</summary>
    public string MeetingFolderPath { get; init; } = "";
    public bool VocabularyEnabled { get; init; } = true;
    public IReadOnlyList<VocabularyEntry> VocabularyEntries { get; init; } = Array.Empty<VocabularyEntry>();
    /// <summary>Anweisung für Zusammenfassungen (die App speichert nur Abweichungen vom Standard).</summary>
    public string SummaryPromptTemplate { get; init; } = SummaryPrompt.DefaultTemplate;
    /// <summary>Standardsprache der Zusammenfassungen. Standard: wie das Meeting.</summary>
    public SummaryLanguage SummaryLanguage { get; init; } = SummaryLanguage.Meeting;
    /// <summary>Hauptschalter: Claude darf Meetings über luna-mcp lesen. Standard aus.</summary>
    public bool McpAccessEnabled { get; init; }
    /// <summary>Claude darf Meetings umbenennen, Sprecher benennen und (nach Bestätigung) löschen. Standard aus.</summary>
    public bool McpWriteEnabled { get; init; }
    /// <summary>Auch Umbenennungen erst nach einem Bestätigungsdialog ausführen. Standard aus.</summary>
    public bool McpConfirmRenames { get; init; }
    /// <summary>Inhaltliche Suche (Embedding-Modell) nutzen. Standard aus, weil sie einen Modell-Download braucht.</summary>
    public bool SemanticSearchEnabled { get; init; }

    /// <summary>Aufgelöster Meeting-Ordner.</summary>
    public string MeetingFolder => AppPaths.MeetingFolder(MeetingFolderPath);

    public bool Equals(SharedSettings? other) =>
        other is not null && MeetingFolderPath == other.MeetingFolderPath && VocabularyEnabled == other.VocabularyEnabled
        && VocabularyEntries.SequenceEqual(other.VocabularyEntries) && SummaryPromptTemplate == other.SummaryPromptTemplate
        && SummaryLanguage == other.SummaryLanguage
        && McpAccessEnabled == other.McpAccessEnabled && McpWriteEnabled == other.McpWriteEnabled
        && McpConfirmRenames == other.McpConfirmRenames && SemanticSearchEnabled == other.SemanticSearchEnabled;

    public override int GetHashCode() => HashCode.Combine(MeetingFolderPath, VocabularyEnabled, VocabularyEntries.Count,
        McpAccessEnabled, McpWriteEnabled, McpConfirmRenames, SemanticSearchEnabled);

    /// <summary>Liest die aktuellen Werte aus der Einstellungsdatei der App (Standard: <see cref="DefaultPath"/>).</summary>
    public static SharedSettings Read(string? path = null)
    {
        path ??= DefaultPath;
        string json;
        try
        {
            if (!File.Exists(path)) return new SharedSettings();
            // Die App schreibt atomar; trotzdem teilend öffnen, um nie an einer Sperre zu scheitern.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SharedSettings();
        }
        return Parse(json);
    }

    /// <summary>Wertet den Inhalt einer settings.json aus; Unlesbares fällt auf die Standardwerte zurück.</summary>
    public static SharedSettings Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return new SharedSettings(); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new SharedSettings();

            var prompt = String(root, Key.SummaryPrompt) ?? "";
            return new SharedSettings
            {
                MeetingFolderPath = String(root, Key.MeetingFolder) ?? "",
                VocabularyEnabled = Bool(root, Key.VocabularyEnabled, true),
                VocabularyEntries = Entries(root),
                SummaryPromptTemplate = prompt.Length == 0 ? SummaryPrompt.DefaultTemplate : prompt,
                SummaryLanguage = SummaryLanguage.FromRawValue(String(root, Key.SummaryLanguage)) ?? SummaryLanguage.Meeting,
                McpAccessEnabled = Bool(root, Key.McpAccessEnabled, false),
                McpWriteEnabled = Bool(root, Key.McpWriteEnabled, false),
                McpConfirmRenames = Bool(root, Key.McpConfirmRenames, false),
                SemanticSearchEnabled = Bool(root, Key.SemanticSearchEnabled, false),
            };
        }
    }

    // MARK: Hilfen

    /// <summary>Eigenschaft nach exaktem Namen, sonst ohne Beachtung der Groß-/Kleinschreibung.</summary>
    private static JsonElement? Find(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var exact)) return exact;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? String(JsonElement root, string key) =>
        Find(root, key) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static bool Bool(JsonElement root, string key, bool fallback) => Find(root, key) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => fallback,
    };

    private static readonly JsonSerializerOptions EntryOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Wie in Swift: ist die Liste unlesbar, gilt sie als leer.</summary>
    private static IReadOnlyList<VocabularyEntry> Entries(JsonElement root)
    {
        if (Find(root, Key.VocabularyEntries) is not { ValueKind: JsonValueKind.Array } array) return Array.Empty<VocabularyEntry>();
        try
        {
            var entries = array.Deserialize<List<VocabularyEntry?>>(EntryOptions) ?? new();
            return entries
                .Where(e => e is not null)
                .Select(e => new VocabularyEntry(e!.Term ?? "", e.Aliases?.Where(a => a is not null) ?? Enumerable.Empty<string>(), e.Id))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Array.Empty<VocabularyEntry>();
        }
    }
}
