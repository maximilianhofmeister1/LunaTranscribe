using System;
using System.IO;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

public class SharedSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private string SettingsPath => Path.Combine(_temp.Path, "settings.json");

    public void Dispose() => _temp.Dispose();

    [Fact(DisplayName = "Fehlende Datei oder kaputtes JSON ergibt die Standardwerte")]
    public void Defaults()
    {
        var expected = new SharedSettings();
        Assert.Equal(expected, SharedSettings.Read(SettingsPath));
        File.WriteAllText(SettingsPath, "{ kaputt");
        Assert.Equal(expected, SharedSettings.Read(SettingsPath));
        File.WriteAllText(SettingsPath, "[]");
        Assert.Equal(expected, SharedSettings.Read(SettingsPath));

        Assert.Equal("", expected.MeetingFolderPath);
        Assert.Equal(AppPaths.DefaultMeetingFolder, expected.MeetingFolder);
        Assert.True(expected.VocabularyEnabled);
        Assert.Empty(expected.VocabularyEntries);
        Assert.Equal(SummaryPrompt.DefaultTemplate, expected.SummaryPromptTemplate);
        Assert.False(expected.McpAccessEnabled);
        Assert.False(expected.McpWriteEnabled);
        Assert.False(expected.McpConfirmRenames);
        Assert.False(expected.SemanticSearchEnabled);
    }

    [Fact(DisplayName = "Liest die Werte der App (PascalCase) neben fremden Schlüsseln")]
    public void ReadsAppFile()
    {
        File.WriteAllText(SettingsPath, """
            {
              "Engine": "parakeet",
              "PrimaryHotkey": { "Key": 1 },
              "MeetingFolder": "C:\\Meetings",
              "VocabularyEnabled": false,
              "VocabularyEntries": [
                { "Id": "5f51a369-c6a9-4aef-8b93-689fa76fe976", "Term": "LunaTranscribe", "Aliases": [ "Luna Transcribe" ] },
                { "Id": "00000000-0000-0000-0000-000000000001", "Term": "weclapp", "Aliases": [] }
              ],
              "SummaryPrompt": "Kurz auf {language}.",
              "McpAccessEnabled": true,
              "McpWriteEnabled": true,
              "McpConfirmRenames": true,
              "SemanticSearchEnabled": true
            }
            """);
        var s = SharedSettings.Read(SettingsPath);
        Assert.Equal(@"C:\Meetings", s.MeetingFolderPath);
        Assert.Equal(@"C:\Meetings", s.MeetingFolder);
        Assert.False(s.VocabularyEnabled);
        Assert.Equal(2, s.VocabularyEntries.Count);
        Assert.Equal(new VocabularyEntry("LunaTranscribe", new[] { "Luna Transcribe" }, Guid.Parse("5f51a369-c6a9-4aef-8b93-689fa76fe976")),
                     s.VocabularyEntries[0]);
        Assert.Equal("Kurz auf {language}.", s.SummaryPromptTemplate);
        Assert.True(s.McpAccessEnabled && s.McpWriteEnabled && s.McpConfirmRenames && s.SemanticSearchEnabled);
    }

    [Fact(DisplayName = "Leerer Prompt und falsche Typen fallen auf den Standard zurück")]
    public void FallbacksPerKey()
    {
        File.WriteAllText(SettingsPath, """
            { "SummaryPrompt": "", "VocabularyEnabled": "ja", "McpAccessEnabled": 1, "VocabularyEntries": [ { "Id": 5 } ] }
            """);
        var s = SharedSettings.Read(SettingsPath);
        Assert.Equal(SummaryPrompt.DefaultTemplate, s.SummaryPromptTemplate);
        Assert.True(s.VocabularyEnabled);
        Assert.False(s.McpAccessEnabled);
        Assert.Empty(s.VocabularyEntries);
    }

    [Fact(DisplayName = "Schlüssel sind gespeicherte Daten und dürfen sich nicht ändern")]
    public void KeysAreStable()
    {
        Assert.Equal("MeetingFolder", SharedSettings.Key.MeetingFolder);
        Assert.Equal("VocabularyEntries", SharedSettings.Key.VocabularyEntries);
        Assert.Equal("SummaryPrompt", SharedSettings.Key.SummaryPrompt);
        Assert.Equal("McpAccessEnabled", SharedSettings.Key.McpAccessEnabled);
        Assert.EndsWith(Path.Combine("LunaTranscribe", "settings.json"), SharedSettings.DefaultPath);
    }
}

public class AppPathsTests
{
    [Fact(DisplayName = "Basisordner überschreibbar, Unterordner wie in der macOS-Version")]
    public void Layout()
    {
        var previous = AppPaths.DataDirectoryOverride;
        try
        {
            AppPaths.DataDirectoryOverride = @"X:\Luna";
            Assert.Equal(@"X:\Luna", AppPaths.DataDirectory);
            Assert.Equal(Path.Combine(@"X:\Luna", "Models"), AppPaths.Models);
            Assert.Equal(Path.Combine(@"X:\Luna", "Unsaved"), AppPaths.UnsavedMeetings);
            Assert.Equal(Path.Combine(@"X:\Luna", "Queue"), AppPaths.MeetingQueue);
            Assert.Equal(Path.Combine(@"X:\Luna", "Index", "search.sqlite"), AppPaths.SearchIndex);
            Assert.Equal(Path.Combine(@"X:\Luna", "mcp-activity.log"), AppPaths.McpActivityLog);
            Assert.Equal(Path.Combine(@"X:\Luna", "Recordings"), AppPaths.MeetingRecordings);
        }
        finally
        {
            AppPaths.DataDirectoryOverride = previous;
        }
        Assert.EndsWith("LunaTranscribe Meetings", AppPaths.DefaultMeetingFolder);
    }

    [Fact(DisplayName = "Meeting-Ordner: leer = Standard, ~ und %VARS% werden aufgelöst")]
    public void MeetingFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(AppPaths.DefaultMeetingFolder, AppPaths.MeetingFolder(""));
        Assert.Equal(home + @"\Meetings", AppPaths.MeetingFolder(@"~\Meetings"));
        Assert.Equal(home + "/Meetings", AppPaths.MeetingFolder("~/Meetings"));
        Assert.Equal(home, AppPaths.MeetingFolder("~"));
        Assert.Equal(@"D:\~x", AppPaths.MeetingFolder(@"D:\~x"));
        Environment.SetEnvironmentVariable("LUNA_TEST_DIR", @"E:\Daten");
        Assert.Equal(@"E:\Daten\Meetings", AppPaths.MeetingFolder(@"%LUNA_TEST_DIR%\Meetings"));
    }

    [Fact(DisplayName = "FNV-1a wie in Swift")]
    public void ContentHashMatchesFnv1a()
    {
        Assert.Equal("cbf29ce484222325", ContentHash.Hex(""));
        Assert.Equal("af63dc4c8601ec8c", ContentHash.Hex("a"));
        Assert.Equal("85944171f73967e8", ContentHash.Hex("foobar"));
    }
}

public class SummaryPromptTests
{
    [Fact(DisplayName = "Standard-Prompt mit Sprache, leerer Text fällt auf den Standard zurück")]
    public void Instructions()
    {
        var de = SummaryPrompt.Instructions(SummaryPrompt.DefaultTemplate, AppLanguage.German);
        Assert.Contains("Schreibe eine Zusammenfassung auf Deutsch in Markdown mit genau diesen Abschnitten (Überschriften auf Deutsch):", de);
        Assert.DoesNotContain("{language}", de);
        Assert.DoesNotContain("\r", de);
        Assert.StartsWith("Du bekommst das Transkript eines Meetings. Es wurde automatisch per Spracherkennung erstellt und kann Hörfehler", de);
        Assert.EndsWith("ohne Einleitung oder Schlusssatz.", de);
        Assert.Equal(SummaryPrompt.Instructions("", AppLanguage.English), SummaryPrompt.Instructions("  \n", AppLanguage.English));
        Assert.Contains("auf Englisch", SummaryPrompt.Instructions(null, AppLanguage.English));
        Assert.Equal(" Auf Französisch! ", SummaryPrompt.Instructions(" Auf {language}! ", AppLanguage.French));
    }
}
