import SwiftUI

@main
struct LunaTranscribeApp: App {
    @StateObject private var state: AppState

    init() {
        // Entwickler-Selbsttests (Meeting/Refine/Vocabulary/Learn/Summary/UI/Queue); beenden den Prozess selbst.
        // Läuft einer, lädt AppState keine Modelle vor – sonst konkurrieren zwei Downloads um dieselbe Datei.
        let selfTestRunning = LearnSelfTest.runIfRequested() || MeetingSelfTest.runIfRequested()
            || RefineSelfTest.runIfRequested() || VocabularySelfTest.runIfRequested() || SummarySelfTest.runIfRequested()
            || UISnapshotSelfTest.isRequested || QueueSelfTest.isRequested
        LunaFont.registerBundledFonts()
        let state = AppState(preloadModels: !selfTestRunning)
        _state = StateObject(wrappedValue: state)
        UISnapshotSelfTest.run(state: state)
        QueueSelfTest.run(state: state)
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarView().environmentObject(state)
        } label: {
            Image(systemName: menuBarSymbol)
        }
        .menuBarExtraStyle(.window)
    }

    /// Diktat-Zustände haben Vorrang (kurzlebig); dahinter zeigt das Icon ein laufendes Meeting.
    private var menuBarSymbol: String {
        switch state.dictation.status {
        case .recording: return "waveform.circle.fill"
        case .transcribing: return "ellipsis.circle"
        case .refining: return "sparkles"
        case .idle, .notice:
            if state.meeting.isRecording { return "record.circle" }
            if state.meetingQueue.isProcessing { return "ellipsis.circle" }
            if case .loading = state.models.state { return "arrow.down.circle.dotted" }
            return "waveform"
        }
    }
}
