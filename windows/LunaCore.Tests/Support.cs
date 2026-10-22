using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LunaCore;

namespace LunaCore.Tests;

/// <summary>Hilfen für Tests: temporäre Ordner, Beispiel-Meetings, Fixtures, Papierkorb-Attrappe.</summary>
internal static class Support
{
    public static DateTime SampleDate { get; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime;

    public static MeetingTranscript Meeting(IEnumerable<(string Speaker, string Text)> lines, string title = "Planung Q4",
                                            DateTime? date = null, string? summary = null, bool confidential = false)
    {
        var when = date ?? SampleDate;
        var turns = lines.Select((line, index) =>
            new MeetingTranscript.Turn(line.Speaker, index * 10, index * 10 + 8, line.Text)).ToList();
        var meeting = new MeetingTranscript(when, turns.Count * 10, EngineKind.Parakeet, AppLanguage.German, turns, title)
        {
            Summary = summary is null ? null : new MeetingSummary(summary, when),
            IsConfidential = confidential ? true : null,
        };
        return meeting;
    }

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string FixtureText(string name) => File.ReadAllText(FixturePath(name)).Replace("\r\n", "\n");
}

/// <summary>Temporärer Ordner, der nach dem Test gelöscht wird.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; }

    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LunaCoreTests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { /* best effort */ }
    }
}

/// <summary>Papierkorb-Attrappe: verschiebt in einen eigenen Ordner statt in den echten Papierkorb.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    private readonly string _folder;
    public List<string> Trashed { get; } = new();

    public FakeRecycleBin(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public void MoveToRecycleBin(string folder)
    {
        Trashed.Add(folder);
        Directory.Move(folder, MeetingLibrary.UniquePath(_folder, System.IO.Path.GetFileName(folder)));
    }
}
