import Testing
@testable import LunaCore

@Suite("Markdown der Zusammenfassungen")
struct MarkdownBlockTests {
    /// Nur die Tiefe jedes Listenpunkts, damit die Fälle unten lesbar bleiben.
    private func depths(_ markdown: String) -> [Int] {
        MarkdownBlock.parse(markdown).compactMap {
            if case .listItem(_, _, let depth) = $0 { depth } else { nil }
        }
    }

    @Test("Überschriften, Punkte, Aufgaben und Absätze werden erkannt")
    func recognizesBlocks() {
        let markdown = """
        # Titel

        ## Kurzfassung
        Ein **wichtiger** Satz.

        - Punkt
        * Sternchen
        + Plus
        • Bullet
        - [ ] Offen
        - [x] Erledigt
        - [X] Auch erledigt
        """
        #expect(MarkdownBlock.parse(markdown) == [
            .heading("Titel", level: 1),
            .heading("Kurzfassung", level: 2),
            .paragraph("Ein **wichtiger** Satz."),
            .listItem("Punkt", marker: .bullet, depth: 0),
            .listItem("Sternchen", marker: .bullet, depth: 0),
            .listItem("Plus", marker: .bullet, depth: 0),
            .listItem("Bullet", marker: .bullet, depth: 0),
            .listItem("Offen", marker: .task(done: false), depth: 0),
            .listItem("Erledigt", marker: .task(done: true), depth: 0),
            .listItem("Auch erledigt", marker: .task(done: true), depth: 0),
        ])
    }

    @Test("Unterpunkte behalten ihre Ebene, auch über Leerzeilen hinweg")
    func keepsNestingAcrossBlankLines() {
        let markdown = """
        ## Themen

        - **Erstes Thema**
          - Aussage
            - Detail
          - Aussage

        - **Zweites Thema**
          - Aussage
        """
        #expect(depths(markdown) == [0, 1, 2, 1, 0, 1])
    }

    @Test("Die Schrittweite der Einrückung ist egal", arguments: ["  ", "    ", "\t"])
    func acceptsAnyIndentStep(step: String) {
        #expect(depths("- a\n\(step)- b\n\(step)\(step)- c\n\(step)- d\n- e") == [0, 1, 2, 1, 0])
    }

    @Test("Eine einzelne Leerstelle macht noch keinen Unterpunkt")
    func singleSpaceIsNoNesting() {
        #expect(depths("- a\n - b\n- c") == [0, 0, 0])
    }

    @Test("Eine durchgehend eingerückte Liste beginnt auf der obersten Ebene")
    func indentedListStartsAtTopLevel() {
        #expect(depths("  - a\n    - b\n  - c") == [0, 1, 0])
    }

    @Test("Ein Rücksprung auf eine Zwischenebene bleibt ein Unterpunkt")
    func outdentToIntermediateLevel() {
        #expect(depths("- a\n    - b\n  - c\n- d") == [0, 1, 1, 0])
    }

    @Test("Überschriften und Absätze beenden die Liste")
    func headingsAndParagraphsEndTheList() {
        #expect(depths("- a\n## Nächster Abschnitt\n  - b") == [0, 0])
        #expect(depths("- a\nText\n  - b") == [0, 0])
    }

    @Test("Aufgaben lassen sich verschachteln")
    func nestedTasks() {
        #expect(MarkdownBlock.parse("- Punkt\n  - [ ] Aufgabe") == [
            .listItem("Punkt", marker: .bullet, depth: 0),
            .listItem("Aufgabe", marker: .task(done: false), depth: 1),
        ])
    }

    @Test("Kein Block: Hashtags, Fettdruck am Zeilenanfang, Leerzeilen")
    func lookalikesStayParagraphs() {
        #expect(MarkdownBlock.parse("#hashtag\n####### zu tief\n**Fett** am Anfang\n\n   \n") == [
            .paragraph("#hashtag"),
            .paragraph("####### zu tief"),
            .paragraph("**Fett** am Anfang"),
        ])
    }

    @Test("Windows-Zeilenenden stören nicht")
    func windowsLineEndings() {
        #expect(depths("- a\r\n  - b\r\n") == [0, 1])
    }
}
