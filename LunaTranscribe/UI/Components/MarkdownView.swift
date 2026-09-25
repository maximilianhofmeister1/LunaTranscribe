import SwiftUI

/// Zeigt das Markdown der Zusammenfassungen: Überschriften, Aufzählungen, Aufgaben-Checkboxen und
/// Absätze mit Inline-Formatierung. Bewusst klein – mehr erzeugt der Zusammenfassungs-Prompt nicht.
struct MarkdownView: View {
    let markdown: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { _, block in
                view(for: block)
            }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private enum Block {
        case heading(String, level: Int)
        case bullet(String)
        case task(String, done: Bool)
        case paragraph(String)
    }

    private var blocks: [Block] {
        markdown.components(separatedBy: .newlines).compactMap { raw in
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard !line.isEmpty else { return nil }
            if line.hasPrefix("#") {
                let level = line.prefix { $0 == "#" }.count
                return .heading(line.dropFirst(level).trimmingCharacters(in: .whitespaces), level: level)
            }
            for (prefix, done) in [("- [ ] ", false), ("- [x] ", true), ("- [X] ", true)] where line.hasPrefix(prefix) {
                return .task(String(line.dropFirst(prefix.count)), done: done)
            }
            if line.hasPrefix("- ") || line.hasPrefix("* ") || line.hasPrefix("• ") {
                return .bullet(String(line.dropFirst(2)))
            }
            return .paragraph(line)
        }
    }

    @ViewBuilder
    private func view(for block: Block) -> some View {
        switch block {
        case .heading(let text, let level):
            Text(inline(text))
                .font(LunaFont.title(level <= 2 ? 18 : 15))
                .padding(.top, 6)
        case .bullet(let text):
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Circle().fill(LunaColor.lavender).frame(width: 5, height: 5).alignmentGuide(.firstTextBaseline) { $0[.bottom] + 1 }
                Text(inline(text)).font(LunaFont.text(13.5))
            }
        case .task(let text, let done):
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: done ? "checkmark.square.fill" : "square").foregroundStyle(LunaColor.honey)
                Text(inline(text)).font(LunaFont.text(13.5))
            }
        case .paragraph(let text):
            Text(inline(text)).font(LunaFont.text(13.5)).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func inline(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
