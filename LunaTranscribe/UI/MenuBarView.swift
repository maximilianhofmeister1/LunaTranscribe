import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var settings = SettingsStore.shared
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            statusRow
            refinerStatusRow

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
                if state.status == .recording {
                    Button("Aufnahme stoppen") { state.toggleRecordingFromMenu() }
                } else {
                    Button("Diktat starten") { state.toggleRecordingFromMenu() }
                        .disabled(!state.models.isReady || state.status == .transcribing || state.status == .refining)
                    Button {
                        state.toggleRecordingFromMenu(refine: true)
                    } label: {
                        Label("Diktat + Nachbearbeitung", systemImage: "sparkles")
                    }
                    .disabled(!state.models.isReady || state.status == .transcribing || state.status == .refining)
                    .help(state.refiners.isReady ? "Diktat wird vom Sprachmodell bereinigt (wie Hotkey 3)"
                                                 : "Sprachmodell lädt noch – bis dahin wird der Rohtext eingefügt")
                }
                if state.status != .idle {
                    Button("Abbrechen (Esc)") { state.cancel() }
                }
            }

            Divider()
            meetingSection

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

    /// Sprachmodell für die Nachbearbeitung – nur sichtbar, solange es lädt oder fehlgeschlagen ist.
    @ViewBuilder
    private var refinerStatusRow: some View {
        switch state.refiners.state {
        case .loading:
            HStack(spacing: 8) {
                Image(systemName: "sparkles").foregroundStyle(.secondary)
                Text(state.refiners.statusText).font(.caption).foregroundStyle(.secondary)
            }
        case .failed(let message):
            HStack(spacing: 8) {
                Image(systemName: "sparkles").foregroundStyle(.orange)
                Text("Nachbearbeitung: \(message)").font(.caption).foregroundStyle(.secondary).lineLimit(2)
            }
        case .idle, .ready:
            EmptyView()
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

    /// Meeting-Modus: Start/Stopp, Laufzeit, Verarbeitungsfortschritt. Läuft parallel zum Diktat.
    @ViewBuilder
    private var meetingSection: some View {
        let meeting = state.meeting
        VStack(alignment: .leading, spacing: 6) {
            Text("Meeting").font(.caption.bold()).foregroundStyle(.secondary)
            switch meeting.status {
            case .idle:
                HStack {
                    Button("Meeting starten") { meeting.start() }
                        .disabled(!state.models.isReady)
                    if meeting.transcript != nil {
                        Button("Letztes Transkript…") { state.showMeetingResult() }
                    }
                }
                speakerCountPicker
            case .recording:
                HStack(spacing: 8) {
                    Image(systemName: "record.circle.fill").foregroundStyle(.red)
                    Text("Aufnahme \(MeetingTranscript.clock(meeting.elapsed))").monospacedDigit()
                    Spacer()
                    Button("Beenden") { meeting.stop() }
                    Button("Verwerfen") { meeting.cancel() }
                }
                Text(meeting.systemAudioActive ? "Mikrofon + Systemaudio · Diktieren weiterhin möglich"
                                               : "Nur Mikrofon · Diktieren weiterhin möglich")
                    .font(.caption).foregroundStyle(.secondary)
                if settings.meetingCaptureSystemAudio && !meeting.systemAudioActive {
                    Text("Systemaudio nicht verfügbar – Berechtigung prüfen.")
                        .font(.caption).foregroundStyle(.orange)
                }
                speakerCountPicker   // darf bis zum Beenden noch geändert werden
            case .processing(let step, let fraction):
                ProgressView(value: fraction) { Text(step).font(.caption) }
                Button("Abbrechen") { meeting.cancel() }
            case .failed(let message):
                Label(message, systemImage: "xmark.circle").font(.caption).foregroundStyle(.red)
                HStack {
                    Button("OK") { meeting.dismissError() }
                    if settings.meetingCaptureSystemAudio {
                        Button("Systemaudio-Einstellungen…") { Permissions.openSystemAudioSettings() }
                    }
                }
            }
        }
    }

    /// Bekannte Teilnehmerzahl hilft der Sprechererkennung enorm (bei leisen/ähnlichen Stimmen
    /// landet sonst alles bei einem Sprecher). 0 = automatisch.
    private var speakerCountPicker: some View {
        Picker("Teilnehmer", selection: $settings.meetingSpeakerCount) {
            Text("automatisch").tag(0)
            ForEach(2...8, id: \.self) { Text("\($0)").tag($0) }
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
                        if t.rawText != nil {
                            Image(systemName: "sparkles").font(.caption2).foregroundStyle(.secondary)
                        }
                        Text(t.text).lineLimit(1).font(.caption)
                        Spacer()
                        Text(String(format: "%.1fs", t.processingSeconds)).font(.caption2).foregroundStyle(.tertiary)
                    }
                }
                .buttonStyle(.plain)
                .help(t.rawText.map { "Klicken kopiert den Text in die Zwischenablage\n\nRohtext: \($0)" }
                      ?? "Klicken kopiert den Text in die Zwischenablage")
            }
        }
    }

    private var statusText: String {
        guard case .ready = state.models.state else { return state.models.statusText }
        switch state.status {
        case .recording: return "Aufnahme läuft"
        case .transcribing: return "Transkribiere…"
        case .refining: return "Formuliere…"
        case .notice(let t): return t
        case .idle: return state.models.statusText
        }
    }

    private var statusSymbol: String {
        if case .recording = state.status { return "record.circle.fill" }
        if case .transcribing = state.status { return "waveform.circle" }
        if case .refining = state.status { return "sparkles" }
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
