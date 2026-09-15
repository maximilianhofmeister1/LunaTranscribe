import SwiftUI

@main
struct LunaTranscribeApp: App {
    @StateObject private var state = AppState()

    init() {
        // Entwickler-Selbsttest der Meeting-Pipeline (siehe MeetingSelfTest); beendet den Prozess selbst.
        _ = MeetingSelfTest.runIfRequested()
    }

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

    /// Diktat-Zustände haben Vorrang (kurzlebig); dahinter zeigt das Icon ein laufendes Meeting.
    private var menuBarSymbol: String {
        switch state.status {
        case .recording: return "waveform.circle.fill"
        case .transcribing: return "ellipsis.circle"
        default:
            if state.meeting.isRecording { return "record.circle" }
            if state.meeting.isProcessing { return "ellipsis.circle" }
            if case .loading = state.models.state { return "arrow.down.circle.dotted" }
            return "waveform"
        }
    }
}
