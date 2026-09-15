import AppKit
import Carbon.HIToolbox

/// Fügt Text in die gerade aktive App ein: Zwischenablage setzen → ⌘V simulieren → Zwischenablage wiederherstellen.
enum TextInjector {
    @MainActor
    static func insert(_ text: String) {
        let pasteboard = NSPasteboard.general

        // Vorherigen Inhalt sichern (nur Textformate; andere Inhalte bleiben unangetastet)
        let previous = pasteboard.pasteboardItems?.compactMap { item -> [NSPasteboard.PasteboardType: Data]? in
            var dict: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                if let data = item.data(forType: type) { dict[type] = data }
            }
            return dict.isEmpty ? nil : dict
        } ?? []

        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)

        postCommandV()

        // Nach kurzer Zeit alten Inhalt zurücklegen, damit der Nutzer nichts verliert.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            guard pasteboard.string(forType: .string) == text else { return }
            pasteboard.clearContents()
            guard !previous.isEmpty else { return }
            let items: [NSPasteboardItem] = previous.map { dict in
                let item = NSPasteboardItem()
                for (type, data) in dict { item.setData(data, forType: type) }
                return item
            }
            pasteboard.writeObjects(items)
        }
    }

    private static func postCommandV() {
        let source = CGEventSource(stateID: .combinedSessionState)
        let vKey = CGKeyCode(kVK_ANSI_V)

        guard let down = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: false) else { return }
        down.flags = .maskCommand
        up.flags = .maskCommand
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
}
