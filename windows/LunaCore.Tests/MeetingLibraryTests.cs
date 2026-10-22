using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

public class MeetingLibraryTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private string Library => Path.Combine(_temp.Path, "Bibliothek");
    private FakeRecycleBin NewRecycleBin() => new(Path.Combine(_temp.Path, "Papierkorb"));

    public void Dispose() => _temp.Dispose();

    [Fact(DisplayName = "Schreiben, Lesen, Umbenennen mit Ordnerwechsel")]
    public void WriteReadRename()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Hallo zusammen.") }, summary: "## Kurzfassung\nKurz.");
        var folder = MeetingLibrary.Write(meeting, Library, existing: null);
        Assert.EndsWith($" · {MeetingLibrary.ShortID(meeting.Id)}", Path.GetFileName(folder));
        Assert.Equal(MeetingLibrary.FolderName(meeting), Path.GetFileName(folder));
        Assert.StartsWith(MeetingTranscript.Formatters.FileDate(meeting.Date) + " Planung Q4 · ", Path.GetFileName(folder));
        Assert.Equal("# Planung Q4 – Zusammenfassung\n\n## Kurzfassung\nKurz.\n",
                     File.ReadAllText(Path.Combine(folder, MeetingLibrary.SummaryFileName)));

        var stored = MeetingLibrary.Read(folder);
        Assert.Equal(meeting, stored.Transcript);
        Assert.False(stored.HasAudio);
        Assert.Equal(ContentHash.Hex(File.ReadAllBytes(Path.Combine(folder, MeetingLibrary.TranscriptFileName))), stored.Revision);

        var renamed = MeetingLibrary.Update(folder, stored.Revision, t => t.Title = "Neuer Titel");
        Assert.Equal("Neuer Titel", renamed.Transcript.Title);
        Assert.Contains("Neuer Titel", Path.GetFileName(renamed.Folder));
        Assert.False(Directory.Exists(folder));
        Assert.NotEqual(stored.Revision, renamed.Revision);
        Assert.StartsWith("# Neuer Titel – Zusammenfassung", File.ReadAllText(Path.Combine(renamed.Folder, MeetingLibrary.SummaryFileName)));
    }

    [Fact(DisplayName = "Ohne Zusammenfassung verschwindet summary.md")]
    public void RemovesSummaryFile()
    {
        var folder = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }, summary: "Kurz."), Library, null);
        var stored = MeetingLibrary.Read(folder);
        var updated = MeetingLibrary.Update(folder, stored.Revision, t => t.Summary = null);
        Assert.False(File.Exists(Path.Combine(updated.Folder, MeetingLibrary.SummaryFileName)));
        Assert.Single(Directory.GetFiles(updated.Folder));   // keine temporären Dateien übrig
    }

    [Fact(DisplayName = "Nur Groß-/Kleinschreibung im Titel geändert: Ordner wird sauber umbenannt")]
    public void RenameCaseOnly()
    {
        var folder = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }, title: "planung"), Library, null);
        var updated = MeetingLibrary.Update(folder, MeetingLibrary.Read(folder).Revision, t => t.Title = "Planung");
        Assert.Contains(" Planung · ", Path.GetFileName(updated.Folder));
        Assert.Single(Directory.GetDirectories(Library));
        Assert.Equal(Path.GetFileName(updated.Folder), Path.GetFileName(Directory.GetDirectories(Library)[0]));
    }

    [Fact(DisplayName = "Gleicher Ordnername bekommt eine Nummer")]
    public void UniqueNames()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Text") });
        var first = MeetingLibrary.Write(meeting, Library, null);
        var second = MeetingLibrary.Write(meeting, Library, null);
        Assert.Equal(first + " (2)", second);
        Assert.Equal(2, MeetingLibrary.Scan(Library).Count);
    }

    [Fact(DisplayName = "Veraltete Revision wird abgelehnt")]
    public void RejectsStaleRevision()
    {
        var folder = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }), Library, null);
        var stale = MeetingLibrary.Read(folder).Revision;
        var current = MeetingLibrary.Update(folder, stale, t => t.SpeakerNames["Sprecher 1"] = "Anna");

        var e1 = Assert.Throws<RevisionMismatchException>(() => MeetingLibrary.Update(current.Folder, stale, t => t.Title = "Egal"));
        Assert.Equal(current.Revision, e1.Current);
        Assert.Equal("Das Meeting wurde inzwischen geändert.", e1.Message);
        var e2 = Assert.Throws<RevisionMismatchException>(() => MeetingLibrary.MoveToTrash(current.Folder, stale, NewRecycleBin()));
        Assert.Equal(current.Revision, e2.Current);
    }

    [Fact(DisplayName = "Papierkorb entfernt den Ordner aus der Bibliothek")]
    public void Trash()
    {
        var bin = NewRecycleBin();
        var folder = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }), Library, null);
        var revision = MeetingLibrary.Read(folder).Revision;
        MeetingLibrary.MoveToTrash(folder, revision, bin);
        Assert.Equal(new[] { folder }, bin.Trashed);
        Assert.Empty(MeetingLibrary.Scan(Library));
        Assert.Throws<MeetingNotFoundException>(() => MeetingLibrary.MoveToTrash(folder, recycleBin: bin));
        Assert.Throws<MeetingNotFoundException>(() => MeetingLibrary.Update(folder, revision, _ => { }));
    }

    [Fact(DisplayName = "Scan: Audio, Rettungsordner, unlesbare Meetings, Ordner ohne meeting.json")]
    public void ScanReportsErrors()
    {
        var good = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }), Library, null);
        File.WriteAllBytes(Path.Combine(good, MeetingLibrary.AudioFileName), new byte[] { 1 });
        var broken = Path.Combine(Library, "kaputt");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, MeetingLibrary.TranscriptFileName), "{ kein json");
        Directory.CreateDirectory(Path.Combine(Library, "leer"));

        var errors = new List<string>();
        var found = MeetingLibrary.Scan(Library, unsaved: true, onError: (f, _) => errors.Add(f));
        var meeting = Assert.Single(found);
        Assert.True(meeting.HasAudio);
        Assert.True(meeting.IsUnsaved);
        Assert.Equal(new[] { broken }, errors);
        Assert.Empty(MeetingLibrary.Scan(Path.Combine(_temp.Path, "gibt-es-nicht")));
    }

    [Fact(DisplayName = "Symbolische Links und Junctions werden nicht als Meetings gelesen")]
    public void SkipsLinks()
    {
        var outside = Path.Combine(_temp.Path, "Außerhalb");
        var folder = MeetingLibrary.Write(Support.Meeting(new[] { ("Sprecher 1", "Text") }), outside, null);
        Directory.CreateDirectory(Library);
        var link = Path.Combine(Library, "link");
        try
        {
            Directory.CreateSymbolicLink(link, folder);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            // Symlinks brauchen unter Windows Entwicklermodus oder Adminrechte; eine Junction geht immer.
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{folder}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            p.WaitForExit();
        }
        Assert.True(File.Exists(Path.Combine(link, MeetingLibrary.TranscriptFileName)));
        Assert.Empty(MeetingLibrary.Scan(Library));
        Directory.Delete(link);   // nur der Link, nicht das Ziel
    }

    [Fact(DisplayName = "Zusammenführen: lokale Änderungen gewinnen, sonst der Stand der Platte")]
    public void ThreeWayMerge()
    {
        var @base = Support.Meeting(new[] { ("Sprecher 1", "Hallo"), ("Sprecher 2", "Tschüss") });
        @base.SpeakerNames = new() { ["Sprecher 1"] = "Anna" };

        var disk = @base.Clone();     // von luna-mcp geändert
        disk.Title = "Von Claude";
        disk.SpeakerNames["Sprecher 2"] = "Tom";

        var local = @base.Clone();    // vom Nutzer in der App geändert
        local.Turns[0] = local.Turns[0] with { Text = "Hallo zusammen" };
        local.SpeakerNames["Sprecher 1"] = "Anne";

        var merged = MeetingLibrary.Merge(local, @base, disk);
        Assert.Equal("Von Claude", merged.Title);
        Assert.Equal("Hallo zusammen", merged.Turns[0].Text);
        Assert.Equal(new Dictionary<string, string> { ["Sprecher 1"] = "Anne", ["Sprecher 2"] = "Tom" }, merged.SpeakerNames);
        Assert.Equal("Planung Q4", local.Title);   // Eingaben bleiben unverändert
    }

    [Fact(DisplayName = "Zusammenführen: lokales Zurücksetzen eines Namens gewinnt, Platte gewinnt bei Turns und Vertraulichkeit")]
    public void MergeResetAndDiskWins()
    {
        var @base = Support.Meeting(new[] { ("Sprecher 1", "Hallo") });
        @base.SpeakerNames = new() { ["Sprecher 1"] = "Anna" };
        var disk = @base.Clone();
        disk.Turns[0] = disk.Turns[0] with { Text = "Von der Platte" };
        disk.IsConfidential = true;
        disk.Summary = new MeetingSummary("Neu", Support.SampleDate);
        var local = @base.Clone();
        local.SpeakerNames.Remove("Sprecher 1");

        var merged = MeetingLibrary.Merge(local, @base, disk);
        Assert.Empty(merged.SpeakerNames);
        Assert.Equal("Von der Platte", merged.Turns[0].Text);
        Assert.True(merged.Confidential);
        Assert.Equal("Neu", merged.Summary!.Markdown);
    }
}

public class ActivityLogTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact(DisplayName = "Anhängen, lesen (neueste zuerst), aufräumen")]
    public void AppendReadCompact()
    {
        var log = new ActivityLog(Path.Combine(_temp.Path, "sub", "log.jsonl"));
        var id = Guid.NewGuid();
        log.Append(new ActivityEntry("test", ActivityAction.Read, new[] { id }, date: DateTime.UtcNow.AddDays(-40)));
        log.Append(new ActivityEntry("test", ActivityAction.RenameMeeting, new[] { id }, oldValue: "Alt", newValue: "Neu"));
        Assert.Equal(new[] { ActivityAction.RenameMeeting, ActivityAction.Read }, log.Entries().Select(e => e.Action));
        Assert.True(log.Entries()[0].IsChange);
        Assert.False(log.Entries()[1].IsChange);
        Assert.Single(log.Entries(limit: 1));

        log.Compact();
        Assert.Equal(new[] { ActivityAction.RenameMeeting }, log.Entries().Select(e => e.Action));
    }

    [Fact(DisplayName = "Zeilenformat wie in Swift; beschädigte Zeilen werden übersprungen")]
    public void LineFormat()
    {
        var path = Path.Combine(_temp.Path, "log.jsonl");
        var log = new ActivityLog(path);
        var id = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
        var date = new DateTime(2026, 9, 25, 14, 3, 0, DateTimeKind.Utc);
        log.Append(new ActivityEntry("claude-code", ActivityAction.RenameSpeaker, new[] { id }, date, "Sprecher 1", null, "Anna"));
        Assert.Equal("{\"action\":\"renameSpeaker\",\"client\":\"claude-code\",\"date\":\"2026-09-25T14:03:00Z\","
                     + "\"meetingIDs\":[\"A1B2C3D4-0000-4000-8000-000000000001\"],\"newValue\":\"Anna\",\"speakerID\":\"Sprecher 1\"}\n",
                     File.ReadAllText(path));

        File.AppendAllText(path, "kaputt\n{\"action\":\"unbekannt\",\"client\":\"x\",\"date\":\"2026-09-25T14:03:00Z\",\"meetingIDs\":[]}\n");
        log.Append(new ActivityEntry("claude-ai", ActivityAction.DeleteDeclined, new[] { id }, date));
        var entries = log.Entries();
        Assert.Equal(2, entries.Count);
        Assert.Equal(ActivityAction.DeleteDeclined, entries[0].Action);
        Assert.Equal(new ActivityEntry("claude-code", ActivityAction.RenameSpeaker, new[] { id }, date, "Sprecher 1", null, "Anna"), entries[1]);
    }

    [Fact(DisplayName = "Fehlende Datei ergibt keine Einträge")]
    public void MissingFile() => Assert.Empty(new ActivityLog(Path.Combine(_temp.Path, "nichts.log")).Entries());
}
