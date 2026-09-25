import SwiftUI

/// Popover der Menüleiste: Status, Diktat, Meeting, letzte Diktate, Zugang zum Hauptfenster.
struct MenuBarView: View {
    @EnvironmentObject var state: AppState

    private var dictation: DictationController { state.dictation }
    private var meeting: MeetingSession { state.meeting }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if !state.accessibilityGranted { accessibilityWarning }
            dictationCard
            meetingCard
            if state.settings.keepHistory, !dictation.history.isEmpty { historyCard }
            footer
        }
        .padding(14)
        .frame(width: 320)
        .background(LunaColor.background)
        .font(LunaFont.text())
        .foregroundStyle(LunaColor.ink)
        .tint(LunaColor.lavender)
    }

    // MARK: Abschnitte

    private var header: some View {
        HStack(spacing: 10) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text("LunaTranscribe").font(LunaFont.display(18))
                Text(statusText).font(LunaFont.caption(11.5)).foregroundStyle(LunaColor.inkMuted).lineLimit(1)
            }
            Spacer()
            if case .loading = state.models.state { ProgressView().controlSize(.small) }
        }
    }

    private var accessibilityWarning: some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Bedienungshilfen fehlen", systemImage: "exclamationmark.triangle.fill")
                .font(LunaFont.text(12.5, weight: .semibold))
                .foregroundStyle(LunaColor.honey)
            Hint("Ohne diese Berechtigung kann der Text nicht in andere Apps eingefügt werden.")
            Button("Berechtigung erteilen…") {
                Permissions.requestAccessibility()
                Permissions.openAccessibilitySettings()
            }
            .buttonStyle(.lunaSecondary)
        }
        .lunaCard(padding: 12)
    }

    private var dictationCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Diktat").font(LunaFont.title(14))
                Spacer()
                Picker("", selection: Binding(get: { state.settings.primaryLanguage }, set: { state.settings.primaryLanguage = $0 })) {
                    ForEach(AppLanguage.supported(by: state.settings.engine)) { Text($0.displayName).tag($0) }
                }
                .labelsHidden()
                .fixedSize()
            }
            HStack(spacing: 6) {
                if dictation.status == .recording {
                    Button { dictation.toggleFromMenu() } label: { Label("Stopp", systemImage: "stop.fill") }
                        .buttonStyle(.lunaPrimary)
                } else {
                    Button { dictation.toggleFromMenu() } label: { Label("Starten", systemImage: "mic.fill") }
                        .buttonStyle(.lunaPrimary)
                        .fixedSize()
                        .disabled(!canDictate)
                    Button { dictation.toggleFromMenu(refine: true) } label: { Label("Nachbearbeiten", systemImage: "sparkles") }
                        .buttonStyle(.lunaSecondary)
                        .fixedSize()
                        .disabled(!canDictate)
                        .help(state.refiners.isReady ? "Diktat wird vom Sprachmodell bereinigt (wie Hotkey 3)"
                                                     : "Sprachmodell lädt noch – bis dahin wird der Rohtext eingefügt")
                }
                if dictation.status != .idle {
                    Button { dictation.cancel() } label: { Image(systemName: "xmark") }
                        .buttonStyle(.lunaQuiet)
                        .help("Abbrechen (Esc)")
                }
            }
            if case .failed = state.refiners.state {
                LoadStatusView(status: state.refiners.loadStatus)
            }
        }
        .lunaCard(padding: 12)
    }

    /// Meeting: Start/Stopp, Laufzeit, Verarbeitungsfortschritt. Läuft parallel zum Diktat.
    private var meetingCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Meeting").font(LunaFont.title(14))
            switch meeting.status {
            case .idle:
                Button { meeting.start() } label: { Label("Starten", systemImage: "person.2.wave.2.fill") }
                    .buttonStyle(.lunaSecondary)
                    .fixedSize()
                    .disabled(!state.models.isReady)
                speakerCountPicker
            case .recording:
                HStack(spacing: 8) {
                    Circle().fill(LunaColor.honey).frame(width: 8, height: 8)
                    Text(MeetingTranscript.clock(meeting.elapsed)).monospacedDigit().font(LunaFont.text(13, weight: .semibold))
                    Spacer()
                    Button("Beenden") { meeting.stop() }.buttonStyle(.lunaPrimary)
                    Button("Verwerfen") { meeting.cancel() }.buttonStyle(.lunaQuiet)
                }
                Hint(meeting.systemAudioActive ? "Mikrofon + Systemaudio · Diktieren weiterhin möglich"
                                               : "Nur Mikrofon · Diktieren weiterhin möglich")
                if state.settings.meetingCaptureSystemAudio && !meeting.systemAudioActive {
                    Text("Systemaudio nicht verfügbar – Berechtigung prüfen.").font(LunaFont.caption(11.5)).foregroundStyle(LunaColor.honey)
                }
                speakerCountPicker   // darf bis zum Beenden noch geändert werden
            case .processing(let step, let fraction):
                ProgressView(value: fraction) { Text(step).font(LunaFont.caption(11.5)) }.tint(LunaColor.lavender)
                Button("Abbrechen") { meeting.cancel() }.buttonStyle(.lunaQuiet)
            case .failed(let message):
                Label(message, systemImage: "xmark.circle").font(LunaFont.caption(11.5)).foregroundStyle(LunaColor.rose)
                HStack {
                    Button("OK") { meeting.dismissError() }.buttonStyle(.lunaSecondary)
                    if state.settings.meetingCaptureSystemAudio {
                        Button("Systemaudio-Einstellungen…") { Permissions.openSystemAudioSettings() }.buttonStyle(.lunaQuiet)
                    }
                }
            }
        }
        .lunaCard(padding: 12)
    }

    /// Bekannte Teilnehmerzahl hilft der Sprechererkennung enorm (bei leisen/ähnlichen Stimmen
    /// landet sonst alles bei einem Sprecher). 0 = automatisch.
    private var speakerCountPicker: some View {
        Picker("Personen", selection: Binding(get: { state.settings.meetingSpeakerCount },
                                              set: { state.settings.meetingSpeakerCount = $0 })) {
            Text("Personen: automatisch").tag(0)
            ForEach(2...8, id: \.self) { Text("\($0) Personen").tag($0) }
        }
        .labelsHidden()
        .font(LunaFont.caption(12))
        .fixedSize()
        .help("Bekannte Teilnehmerzahl verbessert die Sprechererkennung deutlich")
    }

    private var historyCard: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Zuletzt").font(LunaFont.title(14))
                Spacer()
                Button("Alle") { state.openMainWindow(.history) }.buttonStyle(.lunaQuiet)
            }
            ForEach(dictation.history.prefix(3)) { t in
                Button {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(t.text, forType: .string)
                } label: {
                    HStack(spacing: 6) {
                        if t.rawText != nil { Image(systemName: "sparkles").font(.caption2).foregroundStyle(LunaColor.lavender) }
                        Text(t.text).lineLimit(1).font(LunaFont.caption(12))
                        Spacer()
                        Image(systemName: "doc.on.doc").font(.caption2).foregroundStyle(LunaColor.inkMuted)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(t.rawText.map { "Klicken kopiert den Text\n\nRohtext: \($0)" } ?? "Klicken kopiert den Text")
            }
        }
        .lunaCard(padding: 12)
    }

    private var footer: some View {
        HStack(spacing: 6) {
            Button { state.openMainWindow() } label: { Label("Öffnen", systemImage: "macwindow") }
                .buttonStyle(.lunaSecondary)
            Button { state.openSettings() } label: { Image(systemName: "gearshape") }
                .buttonStyle(.lunaSecondary)
                .help("Einstellungen")
            Spacer()
            Button("Beenden") { NSApp.terminate(nil) }.buttonStyle(.lunaQuiet)
        }
    }

    // MARK: Zustand

    private var canDictate: Bool { state.models.isReady && !dictation.status.isBusy }

    private var statusText: String {
        guard case .ready = state.models.state else { return state.models.statusText }
        switch dictation.status {
        case .recording: return "Ich höre zu …"
        case .transcribing: return "Transkribiere …"
        case .refining: return "Formuliere …"
        case .notice(let text): return text
        case .idle: return meeting.isRecording ? "Meeting läuft" : "Bereit · alles lokal"
        }
    }
}
