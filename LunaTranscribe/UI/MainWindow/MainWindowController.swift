import AppKit
import SwiftUI

/// Seiten des Hauptfensters.
enum MainPage: String, CaseIterable, Identifiable {
    case overview, meetings, dictionary, history, settings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .overview: return "Übersicht"
        case .meetings: return "Meetings"
        case .dictionary: return "Wörterbuch"
        case .history: return "Verlauf"
        case .settings: return "Einstellungen"
        }
    }

    var systemImage: String {
        switch self {
        case .overview: return "moon.stars"
        case .meetings: return "person.2.wave.2"
        case .dictionary: return "character.book.closed"
        case .history: return "clock.arrow.circlepath"
        case .settings: return "slider.horizontal.3"
        }
    }
}

/// Bereiche der Einstellungsseite.
enum SettingsSection: String, CaseIterable, Identifiable {
    case general, dictation, refine, meetings, models, privacy

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: return "Allgemein"
        case .dictation: return "Diktat"
        case .refine: return "Nachbearbeitung"
        case .meetings: return "Meetings"
        case .models: return "Modelle"
        case .privacy: return "Datenschutz"
        }
    }
}

/// Navigationszustand des Hauptfensters – von außen steuerbar (Menüleiste, fertiges Meeting).
@MainActor
final class MainRouter: ObservableObject {
    @Published var page: MainPage = .overview
    @Published var selectedMeetingID: UUID?
    @Published var settingsSection: SettingsSection = .general
}

/// Das Hauptfenster als normales `NSWindow`: So lässt es sich aus jedem Kontext öffnen, auch wenn das
/// Menüleisten-Popover zu ist. Die App bleibt eine reine Menüleisten-App (kein Dock-Icon).
@MainActor
final class MainWindowController: NSObject, NSWindowDelegate {
    private unowned let state: AppState
    private var window: NSWindow?

    init(state: AppState) {
        self.state = state
    }

    func show() {
        let window = self.window ?? makeWindow()
        self.window = window
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func makeWindow() -> NSWindow {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1040, height: 700),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.title = "LunaTranscribe"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isReleasedWhenClosed = false
        window.minSize = NSSize(width: 780, height: 520)
        window.contentViewController = NSHostingController(rootView: MainView(router: state.router).environmentObject(state))
        window.delegate = self
        // Größe und Position über Neustarts merken; beim ersten Mal zentrieren.
        if !window.setFrameUsingName("LunaMainWindow") { window.center() }
        window.setFrameAutosaveName("LunaMainWindow")
        return window
    }
}
