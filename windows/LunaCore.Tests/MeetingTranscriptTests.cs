using System;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

public class MeetingTranscriptTests
{
    [Theory(DisplayName = "Uhrzeit: Stunden nur bei langen Meetings")]
    [InlineData(0, "00:00")]
    [InlineData(59.9, "00:59")]
    [InlineData(125.5, "02:05")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3725, "1:02:05")]
    public void Clock(double seconds, string expected) => Assert.Equal(expected, MeetingTranscript.Clock(seconds));

    [Theory(DisplayName = "Dateinamen: verbotene Zeichen, Ränder, Länge")]
    [InlineData("Planung Q4", "Planung Q4")]
    [InlineData("a/b\\c:d?e%f*g|h\"i<j>k", "a-b-c-d-e-f-g-h-i-j-k")]
    [InlineData("  ..Titel.. ", "Titel")]
    [InlineData("Zeile\nzwei\ttab", "Zeile-zwei-tab")]
    [InlineData("...", "Meeting")]
    [InlineData("", "Meeting")]
    public void Sanitized(string input, string expected) => Assert.Equal(expected, FileNames.Sanitized(input));

    [Fact(DisplayName = "Dateinamen werden auf 60 Zeichen gekürzt")]
    public void SanitizedLength()
    {
        Assert.Equal(new string('x', 60), FileNames.Sanitized(new string('x', 80)));
        Assert.Equal("abc", FileNames.Sanitized("abc   def", maxLength: 5));
    }

    [Fact(DisplayName = "Sprecher, Namen, Wörter")]
    public void SpeakersAndWords()
    {
        var t = Support.Meeting(new[] { ("Sprecher 2", "Hallo  zusammen"), ("Sprecher 1", "Ja\nklar."), ("Sprecher 2", "Gut") });
        t.SpeakerNames["Sprecher 1"] = "  ";
        t.SpeakerNames["Sprecher 2"] = " Anna ";
        Assert.Equal(new[] { "Sprecher 2", "Sprecher 1" }, t.SpeakerIDs);
        Assert.Equal("Anna", t.DisplayName("Sprecher 2"));
        Assert.Equal("Sprecher 1", t.DisplayName("Sprecher 1"));
        Assert.Equal(5, t.WordCount);
    }

    [Fact(DisplayName = "Standardtitel und Metadaten in Ortszeit")]
    public void TitleAndMetadata()
    {
        var date = new DateTime(2026, 9, 25, 14, 3, 0, DateTimeKind.Local);
        var t = new MeetingTranscript(date, 3725, EngineKind.Cohere, AppLanguage.English, Array.Empty<MeetingTranscript.Turn>());
        Assert.Equal("Meeting vom 25.09.2026, 14:03", t.Title);
        Assert.Equal("25.09.2026, 14:03 · Dauer 1:02:05 · Cohere Transcribe · Englisch", t.MetadataLine);
        Assert.Equal("2026-09-25 14-03 Meeting vom 25.09.2026, 14-03", t.SuggestedFileName);
    }

    [Fact(DisplayName = "Export als Text und Markdown, mit und ohne Zusammenfassung")]
    public void Render()
    {
        var date = new DateTime(2026, 9, 25, 14, 3, 0, DateTimeKind.Local);
        var t = new MeetingTranscript(date, 70, EngineKind.Parakeet, AppLanguage.German, new[]
        {
            new MeetingTranscript.Turn("Sprecher 1", 0, 5, "Hallo."),
            new MeetingTranscript.Turn("Sprecher 2", 65, 70, "Tschüss."),
        }, title: "Planung");
        t.SpeakerNames["Sprecher 1"] = "Anna";
        t.Summary = new MeetingSummary("## Kurzfassung\nKurz.", date);
        var meta = "25.09.2026, 14:03 · Dauer 01:10 · Parakeet v3 · Deutsch";

        Assert.Equal($"Planung\n{meta}\n\n[00:00] Anna: Hallo.\n[01:05] Sprecher 2: Tschüss.\n",
                     t.Render(MeetingTranscript.ExportFormat.Text));
        Assert.Equal($"Planung\n{meta}\n\nZUSAMMENFASSUNG\n\n## Kurzfassung\nKurz.\n\nTRANSKRIPT\n\n[00:00] Anna: Hallo.\n[01:05] Sprecher 2: Tschüss.\n",
                     t.Render(MeetingTranscript.ExportFormat.Text, includeSummary: true));
        Assert.Equal($"# Planung\n\n_{meta}_\n\n**Anna** (00:00): Hallo.\n\n**Sprecher 2** (01:05): Tschüss.\n",
                     t.Render(MeetingTranscript.ExportFormat.Markdown));
        Assert.Equal($"# Planung\n\n_{meta}_\n\n## Zusammenfassung\n\n### Kurzfassung\nKurz.\n\n## Transkript\n\n**Anna** (00:00): Hallo.\n\n**Sprecher 2** (01:05): Tschüss.\n",
                     t.Render(MeetingTranscript.ExportFormat.Markdown, includeSummary: true));
        Assert.Equal("md", MeetingTranscript.ExportFormat.Markdown.FileExtension());
        Assert.Equal("Text (.txt)", MeetingTranscript.ExportFormat.Text.Label());
    }

    [Fact(DisplayName = "Clone ist unabhängig, Gleichheit vergleicht den Inhalt")]
    public void CloneAndEquality()
    {
        var a = Support.Meeting(new[] { ("Sprecher 1", "Hallo") });
        var b = a.Clone();
        Assert.Equal(a, b);
        b.Turns[0] = b.Turns[0] with { Text = "Hallo zusammen" };
        b.SpeakerNames["Sprecher 1"] = "Anna";
        Assert.NotEqual(a, b);
        Assert.Equal("Hallo", a.Turns[0].Text);
        Assert.Empty(a.SpeakerNames);
    }
}
