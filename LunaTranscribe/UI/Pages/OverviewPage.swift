import KeyboardShortcuts
import SwiftUI

/// Startseite: Begrüßung, heutige Zahlen, Status der Modelle, Tastenkürzel, letzte Meetings.
struct OverviewPage: View {
    @EnvironmentObject var state: AppState

    var body: some View {
        LunaPage(title: greeting, subtitle: "Sprich einfach los – alles bleibt auf diesem Mac.") {
            hero
            HStack(alignment: .top, spacing: LunaMetrics.spacing) {
                statTile(value: "\(wordsToday)", label: "Wörter heute diktiert", systemImage: "text.word.spacing")
                statTile(value: "\(dictationsToday)", label: "Diktate heute", systemImage: "waveform")
                statTile(value: "\(state.meetingStore.entries.count)", label: "Meetings gespeichert", systemImage: "person.2.wave.2")
            }
            HStack(alignment: .top, spacing: LunaMetrics.spacing) {
                modelsCard
                shortcutsCard
            }
            recentMeetingsCard
        }
    }

    // MARK: Abschnitte

    /// Nachthimmel-Kachel mit den zwei Hauptaktionen.
    private var hero: some View {
        HStack(spacing: 20) {
            VStack(alignment: .leading, spacing: 8) {
                Text(heroTitle).font(LunaFont.display(22)).foregroundStyle(LunaColor.moonlight)
                Text(state.settings.recordingMode == .hold
                     ? "Halte \(shortcutText(.recordPrimary)) gedrückt und sprich. Der Text landet dort, wo dein Cursor steht."
                     : "Drück \(shortcutText(.recordPrimary)), sprich und drück noch einmal. Der Text landet dort, wo dein Cursor steht.")
                    .font(LunaFont.text(13))
                    .foregroundStyle(LunaColor.moonlight.opacity(0.75))
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 10) {
                    Button {
                        state.dictation.toggleFromMenu()
                    } label: {
                        Label(state.dictation.status == .recording ? "Aufnahme stoppen" : "Diktat starten", systemImage: "mic.fill")
                    }
                    .buttonStyle(LunaButtonStyle(kind: .primary))
                    .disabled(!state.models.isReady || state.dictation.status.isBusy)

                    Button {
                        state.meeting.isRecording ? state.meeting.stop() : state.meeting.start()
                    } label: {
                        Label(state.meeting.isRecording ? "Meeting beenden" : "Meeting starten", systemImage: "person.2.wave.2.fill")
                    }
                    .buttonStyle(LunaButtonStyle(kind: .secondary))
                    .disabled(!state.models.isReady || state.meeting.isProcessing)
                }
                .padding(.top, 4)
            }
            Spacer(minLength: 0)
            Image(systemName: "moon.stars.fill")
                .font(.system(size: 64))
                .foregroundStyle(LunaColor.moonlight, LunaColor.honey)
                .shadow(color: LunaColor.honey.opacity(0.5), radius: 20)
                .padding(.trailing, 12)
        }
        .padding(22)
        .background(
            LinearGradient(colors: [LunaColor.nightTop, LunaColor.nightBottom], startPoint: .topLeading, endPoint: .bottomTrailing),
            in: RoundedRectangle(cornerRadius: LunaMetrics.cornerRadius + 4, style: .continuous)
        )
        .environment(\.colorScheme, .dark)
    }

    private func statTile(value: String, label: String, systemImage: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Image(systemName: systemImage).foregroundStyle(LunaColor.lavender)
            Text(value).font(LunaFont.display(28))
            Text(label).font(LunaFont.caption(12)).foregroundStyle(LunaColor.inkMuted)
        }
        .lunaCard()
    }

    private var modelsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionTitle(title: "Modelle", systemImage: "cpu")
            row("Spracherkennung") { LoadStatusView(status: state.models.loadStatus) { state.models.retry(state.settings.engine) } }
            row("Nachbearbeitung") { LoadStatusView(status: state.refiners.loadStatus) { state.refiners.retry(state.settings.refinerKind) } }
            if state.settings.vocabularyEnabled, !state.settings.vocabularyEntries.isEmpty {
                row("Wörterbuch") { LoadStatusView(status: state.models.vocabularyStatus) }
            }
            if !state.accessibilityGranted {
                Button("Bedienungshilfen erlauben…") {
                    Permissions.requestAccessibility()
                    Permissions.openAccessibilitySettings()
                }
                .buttonStyle(.lunaDestructive)
                Hint("Ohne diese Berechtigung kann LunaTranscribe keinen Text in andere Apps einfügen.")
            }
        }
        .lunaCard()
    }

    private var shortcutsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionTitle(title: "Tastenkürzel", systemImage: "keyboard")
            row("Diktat · \(state.settings.primaryLanguage.displayName)") { keycap(.recordPrimary) }
            row("Diktat · \(state.settings.secondaryLanguage.displayName)") { keycap(.recordSecondary) }
            row("Diktat + Nachbearbeitung") { keycap(.recordRefined) }
            row("Abbrechen") { Keycap(text: "esc") }
            Button("Ändern…") { state.openSettings(.dictation) }.buttonStyle(.lunaQuiet)
        }
        .lunaCard()
    }

    private var recentMeetingsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                SectionTitle(title: "Letzte Meetings", systemImage: "person.2.wave.2")
                Spacer()
                Button("Alle anzeigen") { state.openMainWindow(.meetings) }.buttonStyle(.lunaQuiet)
            }
            if state.meetingStore.entries.isEmpty {
                Hint("Noch keine Meetings. Starte eins über die Menüleiste – Mikrofon und Systemaudio werden lokal aufgenommen und danach transkribiert.")
            } else {
                ForEach(state.meetingStore.entries.prefix(3)) { entry in
                    Button { state.openMeeting(entry.id) } label: { MeetingRow(entry: entry) }
                        .buttonStyle(.plain)
                }
            }
        }
        .lunaCard()
    }

    // MARK: Helfer

    private func row<Content: View>(_ label: String, @ViewBuilder content: () -> Content) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label).font(LunaFont.text(13)).foregroundStyle(LunaColor.inkMuted)
            Spacer(minLength: 12)
            content()
        }
    }

    private func keycap(_ name: KeyboardShortcuts.Name) -> some View {
        Keycap(text: shortcutText(name))
    }

    private func shortcutText(_ name: KeyboardShortcuts.Name) -> String {
        KeyboardShortcuts.getShortcut(for: name)?.description ?? "–"
    }

    private var today: DailyDictationStats { state.settings.dailyDictationStats.on() }
    private var wordsToday: Int { today.words }
    private var dictationsToday: Int { today.dictations }

    private var greeting: String {
        let hour = Calendar.current.component(.hour, from: Date())
        let firstName = NSFullUserName().split(separator: " ").first.map(String.init) ?? ""
        let salutation: String
        switch hour {
        case 5..<11: salutation = "Guten Morgen"
        case 11..<17: salutation = "Hallo"
        case 17..<22: salutation = "Guten Abend"
        default: salutation = "Gute Nacht"
        }
        return firstName.isEmpty ? salutation : "\(salutation), \(firstName)"
    }

    private var heroTitle: String {
        switch state.dictation.status {
        case .recording: return "Ich höre zu …"
        case .transcribing, .refining: return "Einen Moment …"
        default: return state.models.isReady ? "Bereit, wenn du es bist." : "Die Modelle wachen gerade auf …"
        }
    }
}

/// Tastenkappe für Kürzel-Anzeigen.
struct Keycap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(LunaFont.text(12, weight: .semibold))
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(LunaColor.stroke))
    }
}
