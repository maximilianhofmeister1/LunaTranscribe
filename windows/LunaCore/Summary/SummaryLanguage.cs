using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LunaCore;

/// <summary>
/// In welcher Sprache Zusammenfassungen entstehen: wie das Meeting oder immer in einer festen Sprache. Der Standard
/// steht in den Einstellungen (<see cref="SharedSettings.Key.SummaryLanguage"/>); im Meeting lässt er sich für die nächste
/// Zusammenfassung überschreiben.
///
/// Gespeichert wird der Rohwert: „meeting“ oder der Sprachcode („de“, „en“ …). Rohwerte sind gespeicherte Daten – nie ändern.
/// </summary>
/// <param name="Fixed">Feste Sprache; null = wie das Meeting.</param>
public readonly record struct SummaryLanguage(AppLanguage? Fixed)
{
    private const string MeetingRawValue = "meeting";

    /// <summary>Wie das Meeting (Standard).</summary>
    public static SummaryLanguage Meeting => default;

    public static SummaryLanguage Of(AppLanguage language) => new(language);

    /// <summary>Die tatsächliche Sprache für ein Meeting in <paramref name="meetingLanguage"/>.</summary>
    public AppLanguage Resolved(AppLanguage meetingLanguage) => Fixed ?? meetingLanguage;

    public string RawValue => Fixed is { } language ? language.Code() : MeetingRawValue;

    /// <summary>Liest einen Rohwert; null bei unbekannten Werten.</summary>
    public static SummaryLanguage? FromRawValue(string? rawValue)
    {
        if (rawValue == MeetingRawValue) return Meeting;
        return AppLanguageExtensions.FromCode(rawValue) is { } language ? Of(language) : null;
    }
}

public static class AppLanguageSorting
{
    private static readonly StringComparer German = StringComparer.Create(new CultureInfo("de-DE"), ignoreCase: true);

    /// <summary>Alle Sprachen nach deutschem Anzeigenamen sortiert – für Auswahllisten.</summary>
    public static IReadOnlyList<AppLanguage> SortedByDisplayName { get; } =
        Enum.GetValues<AppLanguage>().OrderBy(l => l.DisplayName(), German).ToList();
}
