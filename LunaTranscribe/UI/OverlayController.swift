import AppKit
import SwiftUI
import Combine

/// Kleines schwebendes Fenster am unteren Bildschirmrand, das Aufnahme und Verarbeitung anzeigt.
/// Nimmt nie den Fokus (sonst würde ⌘V im falschen Fenster landen).
@MainActor
final class OverlayController {
    private var panel: NSPanel?
    private var cancellable: AnyCancellable?

    func bind(to dictation: DictationController) {
        cancellable = dictation.$status
            .removeDuplicates()
            .sink { [weak self] status in
                if status == .idle { self?.hide() } else { self?.show(dictation: dictation) }
            }
    }

    private func show(dictation: DictationController) {
        if panel == nil {
            let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 280, height: 48),
                            styleMask: [.nonactivatingPanel, .borderless],
                            backing: .buffered, defer: false)
            p.level = .statusBar
            p.isOpaque = false
            p.backgroundColor = .clear
            p.hasShadow = false   // Schatten zeichnet die SwiftUI-Pille selbst (weicher)
            p.ignoresMouseEvents = false   // X-Button muss klickbar sein; nonactivating → kein Fokusklau
            p.hidesOnDeactivate = false
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            let host = NSHostingView(rootView: OverlayView(dictation: dictation).padding(16))
            host.sizingOptions = [.intrinsicContentSize]
            p.contentView = host
            panel = p
        }
        guard let panel, let screen = NSScreen.main else { return }
        // Größe folgt dem Inhalt (Hinweistexte sind länger als „Aufnahme…“); zentriert bleiben.
        DispatchQueue.main.async {
            let size = panel.contentView?.fittingSize ?? NSSize(width: 280, height: 48)
            let frame = screen.visibleFrame
            panel.setFrame(NSRect(x: frame.midX - size.width / 2, y: frame.minY + 44,
                                  width: size.width, height: size.height), display: true)
        }
        panel.orderFrontRegardless()
    }

    private func hide() {
        panel?.orderOut(nil)
    }
}

/// Die Pille selbst: Nachthimmel-Verlauf, Mondsymbol, Status, Abbrechen-Knopf.
struct OverlayView: View {
    @ObservedObject var dictation: DictationController

    var body: some View {
        HStack(spacing: 10) {
            indicator
            Text(label)
                .font(LunaFont.text(13, weight: .medium))
                .foregroundStyle(LunaColor.moonlight)
                .lineLimit(2)
                .frame(maxWidth: 320, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)

            Button {
                dictation.cancel()
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 10, weight: .bold))
                    .foregroundStyle(LunaColor.moonlight.opacity(0.7))
                    .frame(width: 20, height: 20)
                    .background(.white.opacity(0.1), in: Circle())
            }
            .buttonStyle(.plain)
            .help("Abbrechen (Esc)")
        }
        .padding(.leading, 12)
        .padding(.trailing, 10)
        .padding(.vertical, 9)
        .background(
            LinearGradient(colors: [LunaColor.nightTop, LunaColor.nightBottom], startPoint: .top, endPoint: .bottom),
            in: Capsule()
        )
        .overlay(Capsule().strokeBorder(.white.opacity(0.12), lineWidth: 1))
        .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
        .fixedSize()
        .environment(\.colorScheme, .dark)
    }

    @ViewBuilder
    private var indicator: some View {
        switch dictation.status {
        case .recording:
            HStack(spacing: 8) {
                Image(systemName: "moon.fill").foregroundStyle(LunaColor.honey)
                WaveformView(level: dictation.inputLevel, color: LunaColor.honey)
            }
        case .transcribing:
            Image(systemName: "moon.stars.fill")
                .foregroundStyle(LunaColor.moonlight)
                .symbolEffect(.pulse, options: .repeating)
        case .refining:
            Image(systemName: "sparkles")
                .foregroundStyle(Color(hex: 0xB9A7F0))
                .symbolEffect(.variableColor.iterative, options: .repeating)
        case .notice:
            Image(systemName: "moon.haze.fill").foregroundStyle(LunaColor.moonlight.opacity(0.8))
        case .idle:
            EmptyView()
        }
    }

    private var label: String {
        switch dictation.status {
        case .recording: return "Ich höre zu…"
        case .transcribing: return "Transkribiere…"
        case .refining: return "Formuliere…"
        case .notice(let text): return text
        case .idle: return ""
        }
    }
}
