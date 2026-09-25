import SwiftUI

@main
struct LunaTranscribeApp: App {
    @StateObject private var state: AppState

    init() {
        // Entwickler-Selbsttests (siehe MeetingSelfTest / RefineSelfTest / VocabularySelfTest); beenden den Prozess selbst.
        // Läuft einer, lädt AppState keine Modelle vor – sonst konkurrieren zwei Downloads um dieselbe Datei.
        let selfTestRunning = MeetingSelfTest.runIfRequested() || RefineSelfTest.runIfRequested()
            || VocabularySelfTest.runIfRequested()
        _state = StateObject(wrappedValue: AppState(preloadModels: !selfTestRunning))
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
        case .refining: return "sparkles"
        default:
            if state.meeting.isRecording { return "record.circle" }
            if state.meeting.isProcessing { return "ellipsis.circle" }
            if case .loading = state.models.state { return "arrow.down.circle.dotted" }
            return "waveform"
        }
    }
}
