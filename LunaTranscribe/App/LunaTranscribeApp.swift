import SwiftUI

@main
struct LunaTranscribeApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        MenuBarExtra {
            MenuBarView().environmentObject(state)
        } label: {
            Image(systemName: menuBarSymbol)
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView().environmentObject(state)
        }
    }

    private var menuBarSymbol: String {
        switch state.status {
        case .recording: return "waveform.circle.fill"
        case .transcribing: return "ellipsis.circle"
        default:
            if case .loading = state.models.state { return "arrow.down.circle.dotted" }
            return "waveform"
        }
    }
}
