import AppKit
import SwiftUI
import Combine

/// Kleines schwebendes Fenster am unteren Bildschirmrand, das Aufnahme / Verarbeitung anzeigt.
/// Nimmt nie den Fokus (sonst würde ⌘V im falschen Fenster landen).
@MainActor
final class OverlayController {
    private var panel: NSPanel?
    private var cancellable: AnyCancellable?

    func bind(to state: AppState) {
        cancellable = state.$status
            .removeDuplicates()
            .sink { [weak self] status in
                switch status {
                case .idle: self?.hide()
                default: self?.show(state: state)
                }
            }
    }

    private func show(state: AppState) {
        if panel == nil {
            let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 260, height: 44),
                            styleMask: [.nonactivatingPanel, .borderless, .hudWindow],
                            backing: .buffered, defer: false)
            p.level = .statusBar
            p.isOpaque = false
            p.backgroundColor = .clear
            p.hasShadow = true
            p.ignoresMouseEvents = false   // X-Button muss klickbar sein; nonactivating → kein Fokusklau
            p.hidesOnDeactivate = false
            p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            p.contentView = NSHostingView(rootView: OverlayView().environmentObject(state))
            panel = p
        }
        guard let panel, let screen = NSScreen.main else { return }
        let size = panel.contentView?.fittingSize ?? NSSize(width: 260, height: 44)
        panel.setContentSize(size)
        let frame = screen.visibleFrame
        panel.setFrameOrigin(NSPoint(x: frame.midX - size.width / 2, y: frame.minY + 60))
        panel.orderFrontRegardless()
    }

    private func hide() {
        panel?.orderOut(nil)
    }
}

struct OverlayView: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        HStack(spacing: 10) {
            switch state.status {
            case .recording:
                Circle().fill(Color.red).frame(width: 10, height: 10)
                Text("Aufnahme…").font(.system(size: 13, weight: .medium))
                LevelMeter(level: state.inputLevel)
            case .transcribing:
                ProgressView().controlSize(.small)
                Text("Transkribiere…").font(.system(size: 13, weight: .medium))
            case .notice(let text):
                Image(systemName: "info.circle")
                Text(text).font(.system(size: 13)).lineLimit(2)
            case .idle:
                EmptyView()
            }

            Button {
                state.cancel()
            } label: {
                Image(systemName: "xmark.circle.fill")
                    .font(.system(size: 15))
                    .foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("Abbrechen (Esc)")
        }
        .padding(.leading, 16)
        .padding(.trailing, 10)
        .padding(.vertical, 10)
        .background(.ultraThinMaterial, in: Capsule())
        .fixedSize()
    }
}

struct LevelMeter: View {
    let level: Float
    var body: some View {
        HStack(spacing: 2) {
            ForEach(0..<8, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1)
                    .fill(Float(i) / 8 < level ? Color.green : Color.secondary.opacity(0.3))
                    .frame(width: 3, height: 12)
            }
        }
    }
}
