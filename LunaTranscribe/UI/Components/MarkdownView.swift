import LunaCore
import SwiftUI

/// Zeigt das Markdown der Zusammenfassungen. Das Zerlegen übernimmt `MarkdownBlock.parse`, hier geht
/// es nur um die Hierarchie im Bild: Unterpunkte rücken unter ihren Oberpunkt und stehen enger
/// beieinander als die Oberpunkte, sodass jedes Thema als Gruppe mit klarer Überschrift lesbar ist.
struct MarkdownView: View {
    private let blocks: [MarkdownBlock]

    init(markdown: String) {
        blocks = MarkdownBlock.parse(markdown)
    }

    private enum Layout {
        static let bodySize: CGFloat = 13.5
        static let markerSize: CGFloat = 5
        static let markerSpacing: CGFloat = 8
        /// Etwas mehr als Punkt + Abstand: Der Unterpunkt beginnt sichtbar rechts vom Text des Oberpunkts.
        static let indentPerLevel: CGFloat = 16
        static let spaceBeforeHeading: CGFloat = 18
        static let spaceBeforeBlock: CGFloat = 8
        static let spaceBeforeSubitem: CGFloat = 4
        static let subitemMarkerOpacity = 0.55
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(blocks.enumerated()), id: \.offset) { index, block in
                view(for: block).padding(.top, index == 0 ? 0 : spaceBefore(block))
            }
        }
        .textSelection(.enabled)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func spaceBefore(_ block: MarkdownBlock) -> CGFloat {
        switch block {
        case .heading: Layout.spaceBeforeHeading
        case .listItem(_, _, let depth) where depth > 0: Layout.spaceBeforeSubitem
        case .listItem, .paragraph: Layout.spaceBeforeBlock
        }
    }

    @ViewBuilder
    private func view(for block: MarkdownBlock) -> some View {
        switch block {
        case .heading(let text, let level):
            Text(inline(text)).font(LunaFont.title(headingSize(level)))
        case .listItem(let text, let marker, let depth):
            HStack(alignment: .firstTextBaseline, spacing: Layout.markerSpacing) {
                markerView(marker, depth: depth)
                bodyText(text)
            }
            .padding(.leading, CGFloat(depth) * Layout.indentPerLevel)
        case .paragraph(let text):
            bodyText(text)
        }
    }

    private func headingSize(_ level: Int) -> CGFloat {
        switch level {
        case 1: 21
        case 2: 18
        default: 15
        }
    }

    @ViewBuilder
    private func markerView(_ marker: MarkdownBlock.ListMarker, depth: Int) -> some View {
        switch marker {
        case .bullet:
            Circle()
                .fill(LunaColor.lavender.opacity(depth == 0 ? 1 : Layout.subitemMarkerOpacity))
                .frame(width: Layout.markerSize, height: Layout.markerSize)
                .alignmentGuide(.firstTextBaseline) { $0[.bottom] + 1 }
        case .task(let done):
            Image(systemName: done ? "checkmark.square.fill" : "square").foregroundStyle(LunaColor.honey)
        }
    }

    private func bodyText(_ text: String) -> some View {
        Text(inline(text)).font(LunaFont.text(Layout.bodySize)).fixedSize(horizontal: false, vertical: true)
    }

    private func inline(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
