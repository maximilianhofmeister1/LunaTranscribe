using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LunaCore;
using LunaMCP.Server;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LunaMCP.Tests;

public class AccessTests
{
    [Fact(DisplayName = "Ohne Freigabe: nur lesende Tools sichtbar, jeder Aufruf erklärt, wo man freischaltet")]
    public async Task Disabled()
    {
        await using var harness = new Harness(new SharedSettings { McpAccessEnabled = false, McpWriteEnabled = true });
        harness.Add(Samples.Planning);
        var client = await harness.ConnectAsync();

        var tools = (await client.ListToolsAsync()).Select(t => t.Name);
        Assert.Equal(new[] { "list_meetings", "search_meetings", "get_meeting", "get_processing_status" }, tools);

        var result = await client.CallAsync("list_meetings");
        Assert.True(result.IsError);
        Assert.Contains("Meetings für Claude freigeben", result.Text);

        Assert.Equal(3, (await client.ListResourcesAsync()).Count);
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ReadResourceAsync("luna://meetings"));
    }

    [Fact(DisplayName = "Schreib-Tools erscheinen erst mit eigenem Schalter")]
    public async Task WriteToggle()
    {
        await using var harness = new Harness();
        var client = await harness.ConnectAsync();
        Assert.Equal(4, (await client.ListToolsAsync()).Count);
        var blocked = await client.CallAsync("rename_meeting", new() { ["id"] = "abcdef", ["title"] = "x", ["revision"] = "r" });
        Assert.True(blocked.IsError);
        Assert.Contains("Claude darf Meetings bearbeiten", blocked.Text);

        harness.Settings = harness.Settings with { McpWriteEnabled = true };
        var listed = await client.ListToolsAsync();
        Assert.Equal(new[] { "rename_meeting", "rename_speaker", "delete_meetings" }, listed.Select(t => t.Name).TakeLast(3));
        Assert.True(listed.First(t => t.Name == "delete_meetings").ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(listed.First(t => t.Name == "list_meetings").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact(DisplayName = "Anweisungen und Serverinfo im Handshake")]
    public async Task Handshake()
    {
        await using var harness = new Harness();
        var client = await harness.ConnectAsync();
        Assert.Equal("lunatranscribe", client.ServerInfo?.Name);
        Assert.Contains("search_meetings", client.ServerInstructions);
        Assert.Contains("Europe/Berlin", client.ServerInstructions);
    }
}

public class ReadTests
{
    [Fact(DisplayName = "list_meetings: neueste zuerst, vertrauliche unsichtbar, Filter und Seiten")]
    public async Task List()
    {
        await using var harness = new Harness();
        harness.Add(Samples.Planning);
        harness.Add(Samples.Retro);
        harness.Add(Samples.Secret);
        var client = await harness.ConnectAsync();

        var all = await client.CallAsync("list_meetings");
        Assert.False(all.IsError);
        Assert.Contains("2 Meeting(s)", all.Text);
        Assert.DoesNotContain("Personalgespräch", all.Text);
        Assert.True(all.Text.IndexOf("Planung Q4", StringComparison.Ordinal) < all.Text.IndexOf("Retro", StringComparison.Ordinal));
        Assert.Equal(2, all.Structured!.Value.GetProperty("total").GetInt32());

        var page = await client.CallAsync("list_meetings", new() { ["limit"] = 1 });
        Assert.Equal("1", page.Structured!.Value.GetProperty("next_cursor").GetString());

        var older = await client.CallAsync("list_meetings", new() { ["to"] = "2026-09-20" });
        Assert.Contains("Retro", older.Text);
        Assert.DoesNotContain("Planung", older.Text);

        var anna = await client.CallAsync("list_meetings", new() { ["speaker"] = "anna" });
        Assert.Contains("Planung", anna.Text);
        Assert.DoesNotContain("Retro", anna.Text);

        var bad = await client.CallAsync("list_meetings", new() { ["from"] = "gestern" });
        Assert.True(bad.IsError);
        Assert.Contains("JJJJ-MM-TT", bad.Text);
    }

    [Fact(DisplayName = "get_meeting: Kopf mit Revision, Sprecherzuordnung, Zusammenfassung, Zeitmarken")]
    public async Task GetMeeting()
    {
        await using var harness = new Harness();
        var folder = harness.Add(Samples.Planning);
        var revision = MeetingLibrary.Read(folder).Revision;
        var client = await harness.ConnectAsync();
        var shortID = MeetingLibrary.ShortID(Samples.Planning.Id);

        var result = await client.CallAsync("get_meeting", new() { ["id"] = shortID });
        Assert.False(result.IsError);
        Assert.Contains("# Planung Q4", result.Text);
        Assert.Contains("Revision " + revision, result.Text);
        Assert.Contains("Sprecher 1 = Anna", result.Text);
        Assert.Contains("### Kurzfassung", result.Text);
        Assert.Contains("[00:30] Sprecher 2: Wir müssen die Preise", result.Text);
        Assert.Equal(ActivityAction.Read, harness.Activity.First().Action);
        Assert.Equal("Testclient", harness.Activity.First().Client);

        var annaOnly = await client.CallAsync("get_meeting", new() { ["id"] = shortID, ["speaker"] = "Anna", ["include"] = new[] { "transcript" } });
        Assert.DoesNotContain("Sprecher 2:", annaOnly.Text);
        Assert.Contains("gefiltert", annaOnly.Text);
        Assert.DoesNotContain("## Zusammenfassung", annaOnly.Text);

        var secret = await client.CallAsync("get_meeting", new() { ["id"] = Samples.Secret.Id.ToString() });
        Assert.True(secret.IsError);
        Assert.Contains("Kein Meeting", secret.Text);

        var tooShort = await client.CallAsync("get_meeting", new() { ["id"] = "abc" });
        Assert.True(tooShort.IsError);
        Assert.Contains("mindestens die 6 Zeichen", tooShort.Text);
    }

    [Fact(DisplayName = "get_meeting kürzt lange Meetings und nennt die Fortsetzung")]
    public async Task Pagination()
    {
        await using var harness = new Harness();
        var lines = Enumerable.Range(0, 200).Select(i => ($"Sprecher {i % 3 + 1}", $"Beitrag Nummer {i} mit etwas Text, damit es sich lohnt.")).ToArray();
        var longMeeting = Samples.Meeting("Langes Meeting", lines);
        harness.Add(longMeeting);
        var client = await harness.ConnectAsync();
        var id = MeetingLibrary.ShortID(longMeeting.Id);

        var first = await client.CallAsync("get_meeting", new() { ["id"] = id, ["max_chars"] = 2000, ["include"] = new[] { "transcript" } });
        Assert.True(first.Text.Length <= 2300);
        var offset = Regex.Match(first.Text, @"offset: (\d+)\)").Groups[1].Value;
        Assert.NotEmpty(offset);
        var second = await client.CallAsync("get_meeting", new() { ["id"] = id, ["offset"] = int.Parse(offset), ["include"] = new[] { "transcript" } });
        Assert.Contains($"Beiträge {offset}–200 von 200", second.Text);

        var window = await client.CallAsync("get_meeting", new() { ["id"] = id, ["from_time"] = "10:00", ["to_time"] = "11:00", ["include"] = new[] { "transcript" } });
        Assert.Contains("[10:00]", window.Text);
        Assert.DoesNotContain("[09:30]", window.Text);
        Assert.DoesNotContain("[11:30]", window.Text);
    }

    [Fact(DisplayName = "search_meetings: Stichwort mit Fundstelle, Zusammenfassung, Hinweis ohne inhaltliche Suche")]
    public async Task KeywordSearch()
    {
        await using var harness = new Harness();
        harness.Add(Samples.Planning);
        harness.Add(Samples.Retro);
        harness.Add(Samples.Secret);
        var client = await harness.ConnectAsync();

        var result = await client.CallAsync("search_meetings", new() { ["query"] = "Preise erhöhen" });
        Assert.False(result.IsError);
        Assert.Contains("Planung Q4", result.Text);
        Assert.Contains("**Preise**", result.Text);
        Assert.Contains("Weiterlesen: get_meeting", result.Text);
        Assert.Contains("Inhaltliche Suche ist aus", result.Text);
        Assert.Equal(ActivityAction.Search, harness.Activity.First().Action);

        var budget = await client.CallAsync("search_meetings", new() { ["query"] = "Budget" });
        Assert.DoesNotContain("Personalgespräch", budget.Text);

        var title = await client.CallAsync("search_meetings", new() { ["query"] = "Retro" });
        Assert.Contains("Titel/Sprecher", title.Text);

        var semanticOnly = await client.CallAsync("search_meetings", new() { ["query"] = "teurer", ["mode"] = "semantic" });
        Assert.True(semanticOnly.IsError);
        Assert.Contains("Inhaltliche Suche ist aus", semanticOnly.Text);
    }

    [Fact(DisplayName = "search_meetings: inhaltliche Treffer ohne gemeinsames Wort, fusioniert mit Stichworten")]
    public async Task SemanticSearch()
    {
        var harnessServer = new Harness(new SharedSettings { McpAccessEnabled = true, SemanticSearchEnabled = true }, new SynonymEmbedder());
        await using var harness = harnessServer;
        harness.Add(Samples.Planning);
        harness.Add(Samples.Retro);
        var client = await harness.ConnectAsync();

        // Erster Aufruf startet die Hintergrund-Berechnung; danach sind alle Abschnitte da.
        await client.CallAsync("search_meetings", new() { ["query"] = "Kosten" });
        await Task.Delay(500);
        var result = await client.CallAsync("search_meetings", new() { ["query"] = "Kosten", ["mode"] = "semantic", ["limit"] = 5 });
        Assert.False(result.IsError);
        Assert.Contains("inhaltlich", result.Text);
        Assert.True(result.Text.Contains("Retro Sprint 12") || result.Text.Contains("Planung Q4"));
        Assert.DoesNotContain("indexiert noch", result.Text);
    }

    [Fact(DisplayName = "get_processing_status meldet wartende und fehlgeschlagene Aufträge")]
    public async Task Status()
    {
        await using var harness = new Harness();
        harness.Add(Samples.Planning);
        var job = new MeetingJob(Samples.Date, 600, AppLanguage.German, null, failure: "Modell nicht geladen");
        var folder = Path.Combine(harness.Environment.QueueFolder, job.Id.ToString());
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, MeetingJob.JobFileName), JsonCoding.EncodeToUtf8(job));
        File.WriteAllBytes(Path.Combine(folder, MeetingJob.AudioFileName), Array.Empty<byte>());
        var client = await harness.ConnectAsync();

        var result = await client.CallAsync("get_processing_status");
        Assert.Contains("1 Meeting(s) in der Warteschlange", result.Text);
        Assert.Contains("fehlgeschlagen: Modell nicht geladen", result.Text);
        Assert.Contains("Zuletzt fertig", result.Text);
        Assert.Contains("Suchindex", result.Text);
    }
}

public class WriteTests
{
    private static Harness Writable(bool confirmRenames = false) =>
        new(new SharedSettings { McpAccessEnabled = true, McpWriteEnabled = true, McpConfirmRenames = confirmRenames });

    [Fact(DisplayName = "rename_meeting benennt Titel und Ordner um und protokolliert alt/neu")]
    public async Task RenameMeeting()
    {
        await using var harness = Writable();
        var folder = harness.Add(Samples.Planning);
        var revision = MeetingLibrary.Read(folder).Revision;
        var client = await harness.ConnectAsync();
        var id = MeetingLibrary.ShortID(Samples.Planning.Id);

        var result = await client.CallAsync("rename_meeting", new() { ["id"] = id, ["title"] = "Budget Q4", ["revision"] = revision });
        Assert.False(result.IsError, result.Text);
        var stored = MeetingLibrary.Scan(harness.Library).Single();
        Assert.Equal("Budget Q4", stored.Transcript.Title);
        Assert.Contains("Budget Q4", Path.GetFileName(stored.Folder));
        var entry = harness.Activity.First();
        Assert.Equal(ActivityAction.RenameMeeting, entry.Action);
        Assert.Equal("Planung Q4", entry.OldValue);
        Assert.Equal("Budget Q4", entry.NewValue);
        Assert.Empty(harness.Confirmer.Requests);

        var stale = await client.CallAsync("rename_meeting", new() { ["id"] = id, ["title"] = "Nochmal", ["revision"] = revision });
        Assert.True(stale.IsError);
        Assert.Contains("seit dem Lesen geändert", stale.Text);
        Assert.Equal("Budget Q4", MeetingLibrary.Scan(harness.Library).Single().Transcript.Title);
    }

    [Fact(DisplayName = "rename_speaker per Nummer, Name und Zurücksetzen; Beiträge bleiben unverändert")]
    public async Task RenameSpeaker()
    {
        await using var harness = Writable();
        var folder = harness.Add(Samples.Planning);
        var client = await harness.ConnectAsync();
        var id = MeetingLibrary.ShortID(Samples.Planning.Id);

        var revision = MeetingLibrary.Read(folder).Revision;
        var named = await client.CallAsync("rename_speaker", new() { ["id"] = id, ["speaker"] = "2", ["name"] = "Tom", ["revision"] = revision });
        Assert.False(named.IsError, named.Text);
        var stored = MeetingLibrary.Scan(harness.Library).Single();
        Assert.Equal("Anna", stored.Transcript.SpeakerNames["Sprecher 1"]);
        Assert.Equal("Tom", stored.Transcript.SpeakerNames["Sprecher 2"]);
        Assert.Equal(Samples.Planning.Turns, stored.Transcript.Turns);

        var reset = await client.CallAsync("rename_speaker", new() { ["id"] = id, ["speaker"] = "Anna", ["name"] = "", ["revision"] = stored.Revision });
        Assert.False(reset.IsError, reset.Text);
        stored = MeetingLibrary.Scan(harness.Library).Single();
        Assert.Equal(new[] { "Sprecher 2" }, stored.Transcript.SpeakerNames.Keys);

        var unknown = await client.CallAsync("rename_speaker", new() { ["id"] = id, ["speaker"] = "Bernd", ["name"] = "X", ["revision"] = stored.Revision });
        Assert.True(unknown.IsError);
        Assert.Contains("Vorhanden: Sprecher 1, Sprecher 2 (Tom)", unknown.Text);
    }

    [Fact(DisplayName = "Bestätigung für Umbenennungen, wenn eingeschaltet – Ablehnen ändert nichts")]
    public async Task ConfirmRenames()
    {
        await using var harness = Writable(confirmRenames: true);
        var folder = harness.Add(Samples.Planning);
        var client = await harness.ConnectAsync();
        var revision = MeetingLibrary.Read(folder).Revision;

        var declined = await client.CallAsync("rename_meeting", new() { ["id"] = Samples.Planning.Id.ToString(), ["title"] = "Neu", ["revision"] = revision });
        Assert.True(declined.IsError);
        Assert.Contains("abgelehnt", declined.Text);
        Assert.Single(harness.Confirmer.Requests);
        Assert.Equal("Planung Q4", MeetingLibrary.Read(folder).Transcript.Title);

        harness.Confirmer.Answer = ConfirmationOutcome.Approved;
        var approved = await client.CallAsync("rename_meeting", new() { ["id"] = Samples.Planning.Id.ToString(), ["title"] = "Neu", ["revision"] = revision });
        Assert.False(approved.IsError, approved.Text);
    }

    [Fact(DisplayName = "delete_meetings: Ablehnen löscht nichts und sperrt die Wiederholung")]
    public async Task DeleteDeclined()
    {
        await using var harness = Writable();
        var folder = harness.Add(Samples.Planning);
        var revision = MeetingLibrary.Read(folder).Revision;
        var client = await harness.ConnectAsync();
        var items = new[] { new Dictionary<string, object?> { ["id"] = Samples.Planning.Id.ToString(), ["revision"] = revision } };

        var result = await client.CallAsync("delete_meetings", new() { ["items"] = items });
        Assert.False(result.IsError);
        Assert.Contains("abgelehnt", result.Text);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(ActivityAction.DeleteDeclined, harness.Activity.First().Action);

        var request = Assert.Single(harness.Confirmer.Requests);
        Assert.Equal(ConfirmationKind.Delete, request.Kind);
        Assert.Equal(new[] { "Planung Q4" }, request.Meetings.Select(m => m.Title));
        Assert.Contains("Papierkorb", request.Message("Testclient"));
        Assert.Equal(new[] { "Testclient" }, harness.Confirmer.Clients);

        var again = await client.CallAsync("delete_meetings", new() { ["items"] = items });
        Assert.True(again.IsError);
        Assert.Contains("gerade abgelehnt", again.Text);
        Assert.Single(harness.Confirmer.Requests);
    }

    [Fact(DisplayName = "delete_meetings: nach Zustimmung im Papierkorb, mehrere in einem Dialog")]
    public async Task DeleteApproved()
    {
        await using var harness = Writable();
        var first = harness.Add(Samples.Planning);
        var second = harness.Add(Samples.Retro);
        harness.Confirmer.Answer = ConfirmationOutcome.Approved;
        var client = await harness.ConnectAsync();

        var items = new[] { first, second }.Select(folder =>
        {
            var stored = MeetingLibrary.Read(folder);
            return new Dictionary<string, object?> { ["id"] = MeetingLibrary.ShortID(stored.Id), ["revision"] = stored.Revision };
        }).ToArray();
        var result = await client.CallAsync("delete_meetings", new() { ["items"] = items });
        Assert.False(result.IsError, result.Text);
        Assert.Contains("In den Papierkorb verschoben", result.Text);
        Assert.Empty(MeetingLibrary.Scan(harness.Library));
        Assert.Equal(2, harness.RecycleBin.Trashed.Count);
        Assert.Single(harness.Confirmer.Requests);
        Assert.Equal(ActivityAction.Delete, harness.Activity.First().Action);
        Assert.Equal(2, harness.Activity.First().MeetingIDs.Count);

        var list = await client.CallAsync("list_meetings");
        Assert.Contains("noch keine", list.Text);
    }

    [Fact(DisplayName = "delete_meetings: ohne Dialog (keine interaktive Sitzung) passiert nichts")]
    public async Task DeleteUnavailable()
    {
        await using var harness = Writable();
        var folder = harness.Add(Samples.Planning);
        harness.Confirmer.Answer = ConfirmationOutcome.Unavailable("Keine interaktive Sitzung.");
        var client = await harness.ConnectAsync();
        var revision = MeetingLibrary.Read(folder).Revision;

        var result = await client.CallAsync("delete_meetings", new()
        {
            ["items"] = new[] { new Dictionary<string, object?> { ["id"] = Samples.Planning.Id.ToString(), ["revision"] = revision } },
        });
        Assert.True(result.IsError);
        Assert.Contains("Nichts wurde gelöscht", result.Text);
        Assert.True(Directory.Exists(folder));
    }

    [Fact(DisplayName = "delete_meetings: veraltete Revision → kein Dialog")]
    public async Task DeleteStale()
    {
        await using var harness = Writable();
        harness.Add(Samples.Planning);
        var client = await harness.ConnectAsync();
        var result = await client.CallAsync("delete_meetings", new()
        {
            ["items"] = new[] { new Dictionary<string, object?> { ["id"] = Samples.Planning.Id.ToString(), ["revision"] = "0000" } },
        });
        Assert.True(result.IsError);
        Assert.Contains("seit dem Lesen geändert", result.Text);
        Assert.Empty(harness.Confirmer.Requests);
    }

    [Fact(DisplayName = "Bremse: höchstens 3 Lösch-Dialoge in 10 Minuten, immer nur ein Dialog")]
    public void DeleteThrottle()
    {
        var guardian = new ConfirmationGuard();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            guardian.Begin(new[] { Guid.NewGuid() }, now);
            guardian.End(now);
        }
        Assert.Throws<ToolError>(() => guardian.Begin(new[] { Guid.NewGuid() }, now));
        guardian.Begin(new[] { Guid.NewGuid() }, now.AddSeconds(601));
        Assert.Throws<ToolError>(() => guardian.Begin(null, now));
    }
}

public class ResourcePromptTests
{
    [Fact(DisplayName = "Liste, Vorlagen, Lesen, Wörterbuch, Vervollständigung")]
    public async Task Resources()
    {
        var settings = new SharedSettings
        {
            McpAccessEnabled = true,
            VocabularyEntries = new[] { new VocabularyEntry("LunaTranscribe", new[] { "Luna Transcribe" }) },
        };
        await using var harness = new Harness(settings);
        harness.Add(Samples.Planning);
        harness.Add(Samples.Secret);
        var client = await harness.ConnectAsync();
        var id = MeetingLibrary.ShortID(Samples.Planning.Id);

        var list = await client.ListResourcesAsync();
        Assert.Contains(list, r => r.Uri == "luna://meetings/" + Samples.Planning.Id.ToString().ToLowerInvariant());
        Assert.DoesNotContain(list, r => r.Uri.Contains(Samples.Secret.Id.ToString().ToLowerInvariant()));
        Assert.Equal(3, (await client.ListResourceTemplatesAsync()).Count);

        static string? Text(ReadResourceResult result) => result.Contents.OfType<TextResourceContents>().FirstOrDefault()?.Text;
        Assert.Contains("# Planung Q4", Text(await client.ReadResourceAsync($"luna://meetings/{id}")));
        Assert.DoesNotContain("## Transkript", Text(await client.ReadResourceAsync($"luna://meetings/{id}/summary")));
        var json = await client.ReadResourceAsync($"luna://meetings/{id}/transcript.json");
        Assert.Equal("application/json", json.Contents[0].MimeType);
        Assert.Contains("**LunaTranscribe** (falsch erkannt als: Luna Transcribe)", Text(await client.ReadResourceAsync("luna://vocabulary")));
        await Assert.ThrowsAnyAsync<McpException>(async () => await client.ReadResourceAsync("luna://meetings/" + Samples.Secret.Id));
        await Assert.ThrowsAnyAsync<McpException>(async () => await client.ReadResourceAsync("luna://irgendwas"));

        var completion = await client.CompleteAsync(new ResourceTemplateReference { Uri = "luna://meetings/{id}" }, "id", "plan");
        Assert.Equal(new[] { id }, completion.Completion.Values);
    }

    [Fact(DisplayName = "Prompts: Liste, Zusammenfassung mit eingebettetem Meeting und App-Anweisung, Wochenrückblick")]
    public async Task Prompts()
    {
        await using var harness = new Harness();
        harness.Add(Samples.Planning);
        var client = await harness.ConnectAsync();

        Assert.Equal(new[] { "meeting_summary", "weekly_review", "action_items", "follow_up_email", "find_decision", "name_speakers" },
                     (await client.ListPromptsAsync()).Select(p => p.Name));

        var summary = await client.GetPromptAsync("meeting_summary", new Dictionary<string, object?> { ["id"] = MeetingLibrary.ShortID(Samples.Planning.Id) });
        var embedded = Assert.IsType<EmbeddedResourceBlock>(summary.Messages[0].Content);
        Assert.Contains("# Planung Q4", ((TextResourceContents)embedded.Resource).Text);
        var instructions = Assert.IsType<TextContentBlock>(summary.Messages[^1].Content).Text;
        Assert.Contains("## Kurzfassung", instructions);
        Assert.Contains("auf Deutsch", instructions);

        var week = await client.GetPromptAsync("weekly_review", new Dictionary<string, object?> { ["week"] = "2026-W39" });
        var text = Assert.IsType<TextContentBlock>(week.Messages[0].Content).Text;
        Assert.Contains("vom 21.09.2026 bis 27.09.2026", text);
        Assert.Contains("list_meetings(from: \"2026-09-21\", to: \"2026-09-27\")", text);

        await Assert.ThrowsAnyAsync<McpException>(async () =>
            await client.GetPromptAsync("weekly_review", new Dictionary<string, object?> { ["week"] = "irgendwann" }));
    }
}

public class DiagnosticsTests
{
    [Fact(DisplayName = "Selbsttest besteht ohne Embedding-Modell")]
    public async Task SelfTest()
    {
        var (passed, report) = await Diagnostics.SelfTestAsync(null, "test");
        Assert.True(passed, report);
        Assert.Contains("✓ delete_meetings", report);
        Assert.Contains("Inhaltliche Suche übersprungen", report);
    }

    [Fact(DisplayName = "Selbsttest mit Ersatz-Embedding prüft die inhaltliche Suche")]
    public async Task SelfTestWithEmbedder()
    {
        var (passed, report) = await Diagnostics.SelfTestAsync(new SynonymEmbedder(), "test");
        Assert.Contains("Inhaltliche Suche – „mehr Geld verlangen“", report);
        Assert.Contains("Embedding-Modell", report);
        _ = passed;   // Das Ersatz-Embedding kennt „Geld verlangen“ nicht – maßgeblich ist, dass der Pfad durchläuft.
    }

    [Fact(DisplayName = "Plausibilitätsprüfung erkennt passende und unbrauchbare Vektoren")]
    public async Task EmbeddingSanity()
    {
        Assert.True((await Diagnostics.EmbeddingSanityAsync(new SynonymEmbedder())).Ok);
        Assert.False((await Diagnostics.EmbeddingSanityAsync(new ConstantEmbedder())).Ok);
    }

    [Fact(DisplayName = "check beschreibt die Einrichtung")]
    public async Task Check()
    {
        await using var harness = new Harness(new SharedSettings { McpAccessEnabled = true });
        harness.Add(Samples.Planning);
        harness.Add(Samples.Secret);
        var text = Diagnostics.Check(harness.Environment);
        Assert.Contains("Meetings: 2 (davon 1 vertraulich", text);
        Assert.Contains("Freigabe für Claude: an · Bearbeiten: aus", text);
        Assert.Contains("Suchindex: 1 Meetings", text);
    }

    [Fact(DisplayName = "Bestätigungstexte nennen Client, Meeting und Papierkorb")]
    public void ConfirmationTexts()
    {
        var meeting = MeetingDescription.Of(Samples.Planning);
        var delete = ConfirmationRequest.Delete(new[] { meeting, meeting });
        Assert.Equal("2 Meetings löschen?", delete.Title);
        Assert.Contains("Claude (claude-code) möchte diese Meetings in den Papierkorb verschieben", delete.Message("claude-code"));
        Assert.True(delete.IsDestructive);
        var speaker = ConfirmationRequest.RenameSpeaker(meeting, "Sprecher 2", "");
        Assert.Contains("auf den ursprünglichen Namen zurücksetzen", speaker.Message("x"));
        Assert.Equal("Benennen", speaker.ConfirmButton);
    }
}
