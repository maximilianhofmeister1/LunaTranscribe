using System.Linq;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

/// <summary>Markdown der Zusammenfassungen (Port der macOS-Tests).</summary>
public class MarkdownBlockTests
{
    /// <summary>Nur die Tiefe jedes Listenpunkts, damit die Fälle unten lesbar bleiben.</summary>
    private static int[] Depths(string markdown) =>
        MarkdownBlock.Parse(markdown).OfType<MarkdownBlock.ListItem>().Select(i => i.Depth).ToArray();

    [Fact(DisplayName = "Überschriften, Punkte, Aufgaben und Absätze werden erkannt")]
    public void RecognizesBlocks()
    {
        const string markdown = "# Titel\n\n## Kurzfassung\nEin **wichtiger** Satz.\n\n- Punkt\n* Sternchen\n+ Plus\n• Bullet\n"
                                + "- [ ] Offen\n- [x] Erledigt\n- [X] Auch erledigt";
        Assert.Equal(new MarkdownBlock[]
        {
            new MarkdownBlock.Heading("Titel", 1),
            new MarkdownBlock.Heading("Kurzfassung", 2),
            new MarkdownBlock.Paragraph("Ein **wichtiger** Satz."),
            new MarkdownBlock.ListItem("Punkt", MarkdownListMarker.Bullet, 0),
            new MarkdownBlock.ListItem("Sternchen", MarkdownListMarker.Bullet, 0),
            new MarkdownBlock.ListItem("Plus", MarkdownListMarker.Bullet, 0),
            new MarkdownBlock.ListItem("Bullet", MarkdownListMarker.Bullet, 0),
            new MarkdownBlock.ListItem("Offen", MarkdownListMarker.TaskOpen, 0),
            new MarkdownBlock.ListItem("Erledigt", MarkdownListMarker.TaskDone, 0),
            new MarkdownBlock.ListItem("Auch erledigt", MarkdownListMarker.TaskDone, 0),
        }, MarkdownBlock.Parse(markdown));
    }

    [Fact(DisplayName = "Unterpunkte behalten ihre Ebene, auch über Leerzeilen hinweg")]
    public void KeepsNestingAcrossBlankLines() =>
        Assert.Equal(new[] { 0, 1, 2, 1, 0, 1 },
            Depths("## Themen\n\n- **Erstes Thema**\n  - Aussage\n    - Detail\n  - Aussage\n\n- **Zweites Thema**\n  - Aussage"));

    [Theory(DisplayName = "Die Schrittweite der Einrückung ist egal")]
    [InlineData("  ")]
    [InlineData("    ")]
    [InlineData("\t")]
    public void AcceptsAnyIndentStep(string step) =>
        Assert.Equal(new[] { 0, 1, 2, 1, 0 }, Depths($"- a\n{step}- b\n{step}{step}- c\n{step}- d\n- e"));

    [Fact(DisplayName = "Eine einzelne Leerstelle macht noch keinen Unterpunkt")]
    public void SingleSpaceIsNoNesting() => Assert.Equal(new[] { 0, 0, 0 }, Depths("- a\n - b\n- c"));

    [Fact(DisplayName = "Eine durchgehend eingerückte Liste beginnt auf der obersten Ebene")]
    public void IndentedListStartsAtTopLevel() => Assert.Equal(new[] { 0, 1, 0 }, Depths("  - a\n    - b\n  - c"));

    [Fact(DisplayName = "Ein Rücksprung auf eine Zwischenebene bleibt ein Unterpunkt")]
    public void OutdentToIntermediateLevel() => Assert.Equal(new[] { 0, 1, 1, 0 }, Depths("- a\n    - b\n  - c\n- d"));

    [Fact(DisplayName = "Überschriften und Absätze beenden die Liste")]
    public void HeadingsAndParagraphsEndTheList()
    {
        Assert.Equal(new[] { 0, 0 }, Depths("- a\n## Nächster Abschnitt\n  - b"));
        Assert.Equal(new[] { 0, 0 }, Depths("- a\nText\n  - b"));
    }

    [Fact(DisplayName = "Aufgaben lassen sich verschachteln")]
    public void NestedTasks() =>
        Assert.Equal(new MarkdownBlock[]
        {
            new MarkdownBlock.ListItem("Punkt", MarkdownListMarker.Bullet, 0),
            new MarkdownBlock.ListItem("Aufgabe", MarkdownListMarker.TaskOpen, 1),
        }, MarkdownBlock.Parse("- Punkt\n  - [ ] Aufgabe"));

    [Fact(DisplayName = "Kein Block: Hashtags, Fettdruck am Zeilenanfang, Leerzeilen")]
    public void LookalikesStayParagraphs() =>
        Assert.Equal(new MarkdownBlock[]
        {
            new MarkdownBlock.Paragraph("#hashtag"),
            new MarkdownBlock.Paragraph("####### zu tief"),
            new MarkdownBlock.Paragraph("**Fett** am Anfang"),
        }, MarkdownBlock.Parse("#hashtag\n####### zu tief\n**Fett** am Anfang\n\n   \n"));

    [Fact(DisplayName = "Windows-Zeilenenden stören nicht")]
    public void WindowsLineEndings() => Assert.Equal(new[] { 0, 1 }, Depths("- a\r\n  - b\r\n"));
}
