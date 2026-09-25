import AppKit
import Carbon.HIToolbox

/// Meldet Escape – global (andere App vorne) und lokal (eigenes Fenster vorne).
/// Die Taste wird nur beobachtet, nie verschluckt.
@MainActor
final class EscapeKeyMonitor {
    private var monitors: [Any] = []

    init(onEscape: @escaping @MainActor () -> Void) {
        let handler: (NSEvent) -> Void = { event in
            guard event.keyCode == UInt16(kVK_Escape) else { return }
            Task { @MainActor in onEscape() }
        }
        if let global = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: handler) {
            monitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { event in
            handler(event)
            return event
        }) {
            monitors.append(local)
        }
    }

    deinit {
        monitors.forEach(NSEvent.removeMonitor)
    }
}
