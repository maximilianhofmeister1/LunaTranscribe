import AppKit
import CoreGraphics
import LunaMCPServer

/// Bestätigung als nativer macOS-Dialog, den luna-mcp selbst zeigt – außerhalb der Reichweite des Modells:
/// Kein Tool-Aufruf und kein Transkript-Text kann ihn bestätigen. „Abbrechen“ ist die Standardtaste,
/// nach `timeout` ohne Antwort gilt die Anfrage als abgelehnt, ohne grafische Sitzung wird nichts geändert.
struct AppKitConfirmer: UserConfirmer {
    var timeout: TimeInterval = 120

    func confirm(_ request: ConfirmationRequest, client: String) async -> ConfirmationOutcome {
        // Ohne Anmeldung am Bildschirm (z. B. Claude Code per SSH) gibt es niemanden, der zustimmen könnte.
        guard CGSessionCopyCurrentDictionary() != nil else {
            return .unavailable("Es ist keine grafische Sitzung aktiv (z. B. Zugriff per SSH).")
        }
        // Der Haupt-Thread wird von `dispatchMain()` bedient (main.swift); dort läuft der modale Dialog.
        return await MainActor.run { Self.runAlert(request, client: client, timeout: timeout) }
    }

    @MainActor
    private static func runAlert(_ request: ConfirmationRequest, client: String, timeout: TimeInterval) -> ConfirmationOutcome {
        let app = NSApplication.shared
        // Kein Dock-Symbol, kein Menü – nur der Dialog.
        app.setActivationPolicy(.accessory)

        let alert = NSAlert()
        alert.alertStyle = request.isDestructive ? .critical : .warning
        alert.messageText = request.title
        alert.informativeText = request.message(client: client)
        alert.icon = appIcon
        // Erste Taste = Standard (Return). Bewusst „Abbrechen“, damit ein versehentliches Return nichts ändert.
        let cancel = alert.addButton(withTitle: "Abbrechen")
        cancel.keyEquivalent = "\r"
        let confirm = alert.addButton(withTitle: request.confirmButton)
        confirm.keyEquivalent = ""
        confirm.hasDestructiveAction = request.isDestructive
        alert.window.level = .floating

        app.activate()
        let timer = Timer(timeInterval: timeout, repeats: false) { _ in app.abortModal() }
        RunLoop.main.add(timer, forMode: .modalPanel)
        let response = alert.runModal()
        timer.invalidate()

        switch response {
        case .alertSecondButtonReturn: return .approved
        case .abort: return .timedOut
        default: return .declined
        }
    }

    /// Das Icon von LunaTranscribe.app (luna-mcp liegt in Contents/MacOS).
    @MainActor
    private static var appIcon: NSImage? {
        let app = Bundle.main.bundleURL
        guard app.pathExtension == "app" else { return nil }
        return NSWorkspace.shared.icon(forFile: app.path)
    }
}
