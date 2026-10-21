using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace LunaCore;

/// <summary>
/// Ein Meeting: Redebeiträge in zeitlicher Reihenfolge plus Metadaten. Wird als <c>meeting.json</c> im
/// Meeting-Ordner gespeichert (<see cref="MeetingLibrary"/>); TXT/Markdown entstehen nur beim Export.
/// Sprecher werden über stabile IDs („Sprecher 1“ …) geführt; <see cref="SpeakerNames"/> erlaubt das Umbenennen,
/// ohne die Turns anzufassen. Gleichheit vergleicht den Inhalt (wie das Swift-Struct); <see cref="Clone"/> kopiert.
/// </summary>
public sealed class MeetingTranscript : IEquatable<MeetingTranscript>
{
    /// <summary>
    /// Version des JSON-Formats. Bei inkompatiblen Änderungen erhöhen und in <see cref="MeetingLibrary"/> migrieren.
    /// Neue optionale Felder (wie <see cref="IsConfidential"/>) brauchen keine neue Version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Ein Redebeitrag. Unveränderlich – Text ändern mit <c>turn with { Text = … }</c>.</summary>
    public sealed record Turn
    {
        [JsonPropertyName("id"), JsonRequired] public Guid Id { get; init; }
        [JsonPropertyName("speakerID"), JsonRequired] public string SpeakerID { get; init; } = "";
        [JsonPropertyName("start"), JsonRequired] public double Start { get; init; }
        [JsonPropertyName("end"), JsonRequired] public double End { get; init; }
        /// <summary>Vom Nutzer korrigierbar; Korrekturen speisen das lernende Wörterbuch.</summary>
        [JsonPropertyName("text"), JsonRequired] public string Text { get; init; } = "";

        public Turn(string speakerID, double start, double end, string text, Guid? id = null)
        {
            Id = id ?? Guid.NewGuid();
            SpeakerID = speakerID;
            Start = start;
            End = end;
            Text = text;
        }

        [JsonConstructor]
        private Turn() { }
    }

    public enum ExportFormat { Text, Markdown }

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("id"), JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("title"), JsonRequired] public string Title { get; set; } = "";
    [JsonPropertyName("date"), JsonRequired] public DateTime Date { get; init; }
    [JsonPropertyName("durationSeconds"), JsonRequired] public double DurationSeconds { get; init; }
    [JsonPropertyName("engine"), JsonRequired] public EngineKind Engine { get; init; }
    [JsonPropertyName("language"), JsonRequired] public AppLanguage Language { get; init; }
    [JsonPropertyName("turns"), JsonRequired] public List<Turn> Turns { get; set; } = new();
    /// <summary>Umbenennungen: Sprecher-ID → Anzeigename. Fehlt ein Eintrag, gilt die ID selbst.</summary>
    [JsonPropertyName("speakerNames")] public Dictionary<string, string> SpeakerNames { get; set; } = new();
    [JsonPropertyName("summary")] public MeetingSummary? Summary { get; set; }
    /// <summary>Dateiname eines alten TXT-Backups, aus dem dieses Meeting importiert wurde (verhindert Doppelimporte).</summary>
    [JsonPropertyName("importedFrom")] public string? ImportedFrom { get; set; }
    /// <summary>
    /// Vertraulich: für Claude (MCP) unsichtbar, nicht im Suchindex, keine Cloud-Zusammenfassung.
    /// Optional, damit ältere Dateien ohne das Feld gültig bleiben (null = nicht vertraulich).
    /// </summary>
    [JsonPropertyName("isConfidential")] public bool? IsConfidential { get; set; }

    public MeetingTranscript(DateTime date, double durationSeconds, EngineKind engine, AppLanguage language,
                             IEnumerable<Turn> turns, string? title = null,
                             IDictionary<string, string>? speakerNames = null, string? importedFrom = null)
    {
        Title = title ?? DefaultTitle(date);
        Date = date;
        DurationSeconds = durationSeconds;
        Engine = engine;
        Language = language;
        Turns = turns.ToList();
        SpeakerNames = speakerNames is null ? new() : new Dictionary<string, string>(speakerNames);
        ImportedFrom = importedFrom;
    }

    [JsonConstructor]
    private MeetingTranscript() { }

    /// <summary>Unabhängige Kopie (Listen und Namen kopiert; Turns und Zusammenfassung sind unveränderlich).</summary>
    public MeetingTranscript Clone()
    {
        var copy = (MeetingTranscript)MemberwiseClone();
        copy.Turns = new List<Turn>(Turns);
        copy.SpeakerNames = new Dictionary<string, string>(SpeakerNames);
        return copy;
    }

    /// <summary>Sprecher-IDs in der Reihenfolge ihres ersten Auftretens.</summary>
    [JsonIgnore]
    public List<string> SpeakerIDs
    {
        get
        {
            var seen = new HashSet<string>();
            return Turns.Where(t => seen.Add(t.SpeakerID)).Select(t => t.SpeakerID).ToList();
        }
    }

    public string DisplayName(string speakerID)
    {
        var custom = SpeakerNames.TryGetValue(speakerID, out var name) ? TextUtil.TrimSpaces(name) : "";
        return custom.Length == 0 ? speakerID : custom;
    }

    [JsonIgnore] public bool Confidential => IsConfidential == true;

    [JsonIgnore] public int WordCount => Turns.Sum(t => TextUtil.SplitWhitespace(t.Text).Count);

    /// <summary>„Meeting vom 25.09.2026, 14:03“ (Ortszeit).</summary>
    public static string DefaultTitle(DateTime date) => $"Meeting vom {Formatters.HeaderDate(date)}";

    // MARK: Export

    /// <summary>Text oder Markdown für Export und Zwischenablage; mit Zusammenfassung vor dem Transkript.</summary>
    public string Render(ExportFormat format, bool includeSummary = false) =>
        format == ExportFormat.Text ? RenderText(includeSummary) : RenderMarkdown(includeSummary);

    /// <summary>Dateiname ohne Endung, z. B. „2026-09-15 14-03 Planung Q4“.</summary>
    [JsonIgnore] public string SuggestedFileName => $"{Formatters.FileDate(Date)} {FileNames.Sanitized(Title)}";

    /// <summary>Kopfzeile mit Metadaten, von allen Formaten gemeinsam genutzt.</summary>
    [JsonIgnore]
    public string MetadataLine =>
        $"{Formatters.HeaderDate(Date)} · Dauer {Clock(DurationSeconds)} · {Engine.ShortLabel()} · {Language.DisplayName()}";

    private string RenderText(bool includeSummary)
    {
        var lines = new List<string> { Title, MetadataLine, "" };
        if (includeSummary && Summary is { } summary)
            lines.AddRange(new[] { "ZUSAMMENFASSUNG", "", summary.Markdown, "", "TRANSKRIPT", "" });
        foreach (var turn in Turns)
            lines.Add($"[{Clock(turn.Start)}] {DisplayName(turn.SpeakerID)}: {turn.Text}");
        return string.Join("\n", lines) + "\n";
    }

    private string RenderMarkdown(bool includeSummary)
    {
        var lines = new List<string> { $"# {Title}", "", $"_{MetadataLine}_", "" };
        if (includeSummary && Summary is { } summary)
            // Überschriften der Zusammenfassung eine Ebene tiefer, damit „# Titel“ oben bleibt.
            lines.AddRange(new[] { "## Zusammenfassung", "", DemoteHeadings(summary.Markdown), "", "## Transkript", "" });
        foreach (var turn in Turns)
        {
            lines.Add($"**{DisplayName(turn.SpeakerID)}** ({Clock(turn.Start)}): {turn.Text}");
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    public static string DemoteHeadings(string markdown) =>
        string.Join("\n", markdown.Split('\n').Select(l => l.StartsWith('#') ? "#" + l : l));

    // MARK: Formatierung

    /// <summary>mm:ss bzw. h:mm:ss – Stunden werden nur gezeigt, wenn das Meeting so lang war.</summary>
    public static string Clock(double seconds)
    {
        long total = (long)Math.Floor(seconds);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
    }

    public static class Formatters
    {
        /// <summary>„2026-09-25 14-03“ (Ortszeit) – für Datei- und Ordnernamen.</summary>
        public static string FileDate(DateTime date) =>
            JsonCoding.ToLocal(date).ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);

        /// <summary>„25.09.2026, 14:03“ (Ortszeit).</summary>
        public static string HeaderDate(DateTime date) =>
            JsonCoding.ToLocal(date).ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture);
    }

    // MARK: Gleichheit

    public bool Equals(MeetingTranscript? other) =>
        other is not null && (ReferenceEquals(this, other) ||
            SchemaVersion == other.SchemaVersion && Id == other.Id && Title == other.Title && Date == other.Date
            && DurationSeconds.Equals(other.DurationSeconds) && Engine == other.Engine && Language == other.Language
            && Turns.SequenceEqual(other.Turns) && SpeakerNamesEqual(SpeakerNames, other.SpeakerNames)
            && Equals(Summary, other.Summary) && ImportedFrom == other.ImportedFrom && IsConfidential == other.IsConfidential);

    public override bool Equals(object? obj) => Equals(obj as MeetingTranscript);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Date, Turns.Count);

    internal static bool SpeakerNamesEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}

public static class ExportFormatExtensions
{
    public static string Label(this MeetingTranscript.ExportFormat f) =>
        f == MeetingTranscript.ExportFormat.Text ? "Text (.txt)" : "Markdown (.md)";

    public static string FileExtension(this MeetingTranscript.ExportFormat f) =>
        f == MeetingTranscript.ExportFormat.Text ? "txt" : "md";
}

/// <summary>Von der Claude CLI erzeugte Zusammenfassung eines Meetings (Markdown).</summary>
public sealed record MeetingSummary
{
    [JsonPropertyName("markdown"), JsonRequired] public string Markdown { get; init; } = "";
    [JsonPropertyName("createdAt"), JsonRequired] public DateTime CreatedAt { get; init; }
    /// <summary>Modell, das in den Einstellungen gewählt war; null = Standard der CLI.</summary>
    [JsonPropertyName("model")] public string? Model { get; init; }

    public MeetingSummary(string markdown, DateTime createdAt, string? model = null)
    {
        Markdown = markdown;
        CreatedAt = createdAt;
        Model = model;
    }

    [JsonConstructor]
    private MeetingSummary() { }
}

public static class FileNames
{
    private const string Forbidden = "/\\:?%*|\"<>";

    /// <summary>Macht einen Titel dateinamen-tauglich: keine Pfadtrenner, keine Steuerzeichen, begrenzte Länge.</summary>
    public static string Sanitized(string name, int maxLength = 60)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(rune);
            bool bad = (rune.IsBmp && Forbidden.Contains((char)rune.Value))
                       || cat is UnicodeCategory.Control or UnicodeCategory.Format
                       || rune.Value is 0x85 or 0x2028 or 0x2029;
            if (bad) sb.Append('-');
            else sb.Append(rune.ToString());
        }
        var cleaned = sb.ToString();
        int start = 0, end = cleaned.Length;
        while (start < end && (TextUtil.IsHorizontalSpace(cleaned[start]) || cleaned[start] == '.')) start++;
        while (end > start && (TextUtil.IsHorizontalSpace(cleaned[end - 1]) || cleaned[end - 1] == '.')) end--;
        var shortened = TextUtil.TrimSpaces(TextUtil.Prefix(cleaned[start..end], maxLength));
        return shortened.Length == 0 ? "Meeting" : shortened;
    }
}
