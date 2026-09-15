import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var settings = SettingsStore.shared
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            statusRow

            if !state.accessibilityGranted {
                warningRow
            }

            Divider()

            Picker("Sprache", selection: $settings.primaryLanguage) {
                ForEach(AppLanguage.supported(by: settings.engine)) { lang in
                    Text(lang.displayName).tag(lang)
                }
            }

            Picker("Engine", selection: $settings.engine) {
                ForEach(EngineKind.allCases) { Text($0.shortLabel).tag($0) }
            }

            HStack {
                Button(state.status == .recording ? "Aufnahme stoppen" : "Aufnahme starten") {
                    state.toggleRecordingFromMenu()
                }
                .disabled(!state.models.isReady || state.status == .transcribing)
                if state.status != .idle {
                    Button("Abbrechen (Esc)") { state.cancel() }
                }
            }

            if !state.history.isEmpty {
                Divider()
                historySection
            }

            Divider()

            HStack {
                Button("Einstellungen…") {
                    openSettings()
                    NSApp.activate(ignoringOtherApps: true)
                }
                Spacer()
                Button("Beenden") { NSApp.terminate(nil) }
            }
        }
        .padding(12)
        .frame(width: 300)
    }

    private var statusRow: some View {
        HStack(spacing: 8) {
            Image(systemName: statusSymbol).foregroundStyle(statusColor)
            Text(statusText).font(.callout)
            Spacer()
        }
    }

    private var warningRow: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label("Bedienungshilfen fehlen", systemImage: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange).font(.callout.bold())
            Text("Ohne diese Berechtigung kann der Text nicht in andere Apps eingefügt werden.")
                .font(.caption).foregroundStyle(.secondary)
            Button("Berechtigung erteilen…") {
                Permissions.requestAccessibility()
                Permissions.openAccessibilitySettings()
            }
        }
    }

    private var historySection: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Letzte Diktate").font(.caption.bold()).foregroundStyle(.secondary)
                Spacer()
                Button("Löschen") { state.clearHistory() }.buttonStyle(.plain).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(state.history.prefix(5)) { t in
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(t.text, forType: .string)
                } label: {
                    HStack {
                        Text(t.text).lineLimit(1).font(.caption)
                        Spacer()
                        Text(String(format: "%.1fs", t.processingSeconds)).font(.caption2).foregroundStyle(.tertiary)
                    }
                }
                .buttonStyle(.plain)
                .help("Klicken kopiert den Text in die Zwischenablage")
            }
        }
    }

    private var statusText: String {
        guard case .ready = state.models.state else { return state.models.statusText }
        switch state.status {
        case .recording: return "Aufnahme läuft"
        case .transcribing: return "Transkribiere…"
        case .notice(let t): return t
        case .idle: return state.models.statusText
        }
    }

    private var statusSymbol: String {
        if case .recording = state.status { return "record.circle.fill" }
        if case .transcribing = state.status { return "waveform.circle" }
        if case .failed = state.models.state { return "xmark.circle" }
        if case .loading = state.models.state { return "arrow.down.circle" }
        return "checkmark.circle"
    }

    private var statusColor: Color {
        if case .recording = state.status { return .red }
        if case .failed = state.models.state { return .red }
        if case .loading = state.models.state { return .secondary }
        return .green
    }
}
