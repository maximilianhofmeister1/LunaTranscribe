using System;
using System.Linq;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

/// <summary>Zusammenfassung: Sprache und Bearbeitung (Port der macOS-Tests).</summary>
public class SummaryTests
{
    [Fact(DisplayName = "Sprachwahl: Rohwerte sind stabil, unbekannte Werte werden abgelehnt")]
    public void SummaryLanguageRawValues()
    {
        Assert.Equal("meeting", SummaryLanguage.Meeting.RawValue);
        Assert.Equal("en", SummaryLanguage.Of(AppLanguage.English).RawValue);
        Assert.Equal(SummaryLanguage.Meeting, SummaryLanguage.FromRawValue("meeting"));
        Assert.Equal(SummaryLanguage.Of(AppLanguage.French), SummaryLanguage.FromRawValue("fr"));
        Assert.Null(SummaryLanguage.FromRawValue("klingonisch"));
        Assert.Null(SummaryLanguage.FromRawValue(null));
        Assert.Equal(new[] { AppLanguage.Arabic, AppLanguage.Bulgarian, AppLanguage.Chinese }, AppLanguageSorting.SortedByDisplayName.Take(3));
    }

    [Fact(DisplayName = "Sprachwahl: „wie das Meeting“ folgt dem Meeting, eine feste Sprache nicht")]
    public void ResolvesLanguage()
    {
        Assert.Equal(AppLanguage.German, SummaryLanguage.Meeting.Resolved(AppLanguage.German));
        Assert.Equal(AppLanguage.English, SummaryLanguage.Of(AppLanguage.English).Resolved(AppLanguage.German));
    }

    [Fact(DisplayName = "Die Anweisung nennt die Zielsprache, auch für Überschriften")]
    public void PromptUsesTargetLanguage()
    {
        var instructions = SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, AppLanguage.English);
        Assert.Contains("auf Englisch", instructions);
        Assert.Contains("auch die Abschnittsüberschriften", instructions);
        Assert.DoesNotContain(SummaryPrompt.Placeholder, instructions);
    }

    [Fact(DisplayName = "Überschriften: bekannte Sprachen fest übersetzt, Deutsch und andere unverändert, eigene Anweisung bleibt")]
    public void LocalizesHeadings()
    {
        var english = SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, AppLanguage.English);
        Assert.Contains("\n## Summary\n", english);
        Assert.Contains("\n## Open Questions\n", english);
        Assert.DoesNotContain("## Kurzfassung", english);
        var german = SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, AppLanguage.German);
        Assert.Contains("\n## Kurzfassung\n", german);
        var czech = SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, AppLanguage.Czech);
        Assert.Contains("\n## Kurzfassung\n", czech);
        Assert.Contains("Überschriften auf Tschechisch", czech);
        Assert.EndsWith("übersetze sie auf Tschechisch.", czech);
        Assert.EndsWith("„## Open Questions“.", english);
        Assert.EndsWith("ohne Einleitung oder Schlusssatz.", german);
        Assert.Equal("Fasse auf Englisch zusammen.", SummaryPrompt.Instructions("Fasse auf {language} zusammen.", AppLanguage.English));
        foreach (var language in Enum.GetValues<AppLanguage>())
            Assert.True(SummaryPrompt.Headings(language) is null or { Length: 5 });
    }

    [Fact(DisplayName = "Bearbeiten setzt Text und Zeitpunkt, behält Herkunft; ohne Änderung bleibt alles gleich")]
    public void Revise()
    {
        var created = Support.SampleDate;
        var edited = created.AddSeconds(60);
        var original = new MeetingSummary("## Kurzfassung\nAlt.", created, "sonnet", AppLanguage.German);

        var revised = original.Revised("  ## Kurzfassung\r\nNeu.\n\n", edited);
        Assert.Equal("## Kurzfassung\nNeu.", revised.Markdown);
        Assert.Equal(edited, revised.EditedAt);
        Assert.True(revised.IsEdited);
        Assert.Equal(created, revised.CreatedAt);
        Assert.Equal("sonnet", revised.Model);
        Assert.Equal(AppLanguage.German, revised.Language);

        Assert.Same(original, original.Revised("## Kurzfassung\nAlt.\n", edited));
        Assert.False(original.IsEdited);
    }

    [Fact(DisplayName = "Ältere Zusammenfassungen ohne Sprache/Bearbeitung bleiben lesbar, null-Felder werden nicht geschrieben")]
    public void CodingIsBackwardCompatible()
    {
        var decoded = JsonCoding.Decode<MeetingSummary>("""{"markdown":"Kurz.","createdAt":"2026-09-21T10:00:00Z","model":"sonnet"}""");
        Assert.Null(decoded.Language);
        Assert.Null(decoded.EditedAt);
        var encoded = JsonCoding.Encode(decoded);
        Assert.DoesNotContain("language", encoded);
        Assert.DoesNotContain("editedAt", encoded);

        var full = decoded.Revised("Neu.", Support.SampleDate) with { Language = AppLanguage.English };
        var json = JsonCoding.Encode(full);
        Assert.Contains("\"language\" : \"en\"", json);
        Assert.Equal(full, JsonCoding.Decode<MeetingSummary>(json));
    }

    [Fact(DisplayName = "Einstellung: fehlender oder ungültiger Wert heißt „wie das Meeting“")]
    public void SharedSettingsReadSummaryLanguage()
    {
        Assert.Equal(SummaryLanguage.Meeting, SharedSettings.Parse("{}").SummaryLanguage);
        Assert.Equal(SummaryLanguage.Of(AppLanguage.English), SharedSettings.Parse("""{"SummaryLanguage":"en"}""").SummaryLanguage);
        Assert.Equal(SummaryLanguage.Meeting, SharedSettings.Parse("""{"SummaryLanguage":"unsinn"}""").SummaryLanguage);
    }
}
