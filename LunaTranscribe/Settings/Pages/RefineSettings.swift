import SwiftUI

struct RefineSettings: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared

    var body: some View {
        SettingsCard(title: "Sprachmodell", systemImage: "sparkles") {
            Picker("", selection: $settings.refinerKind) {
                ForEach(RefinerKind.allCases) { kind in option(kind).tag(kind) }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            LoadStatusView(status: state.refiners.loadStatus) { state.refiners.retry(settings.refinerKind) }
            if settings.refinerKind == .claudeHaiku {
                Hint("Cloud: Jedes nachbearbeitete Diktat geht über die Claude CLI und dein Claude-Konto an Anthropic (Haiku, niedriger Aufwand). Kein API-Schlüssel nötig – die CLI muss installiert und angemeldet sein; den Pfad stellst du unter Meetings → „Zusammenfassung mit Claude“ ein.")
            }
            LearnMore(text: "Der dritte Hotkey lässt ein Sprachmodell das Diktat bereinigen: Rechtschreibung, Grammatik, Füllwörter, Wiederholungen und Versprecher – Wortwahl und Ton bleiben erhalten. Meeting-Transkripte sind nicht betroffen. Schlägt die Nachbearbeitung fehl, wird der Rohtext eingefügt. MLX-Modelle rechnen lokal auf der GPU; Apple Intelligence nutzt das Systemmodell auf der Neural Engine; Claude Haiku läuft in der Cloud.")
        }

        SettingsCard(title: "Anweisung an das Sprachmodell", systemImage: "text.quote") {
            PromptEditor(text: $settings.refinePrompt, isDefault: settings.isRefinePromptDefault, onReset: settings.resetRefinePrompt)
            Hint("Englisch funktioniert bei kleinen Modellen am zuverlässigsten. „{language}“ wird durch die Diktatsprache ersetzt; ein leeres Feld nutzt den Standard. Beispielpaare (Deutsch/Englisch) hängt die App automatisch an.")
        }
    }

    /// Nicht verfügbare Modelle sind ausgegraut und nennen den Grund.
    @ViewBuilder
    private func option(_ kind: RefinerKind) -> some View {
        switch kind.availability {
        case .available:
            Text(kind.label)
        case .unavailable(let reason):
            VStack(alignment: .leading, spacing: 2) {
                Text(kind.label)
                Text(reason).font(LunaFont.caption(11)).foregroundStyle(LunaColor.inkMuted)
            }
            .selectionDisabled()
        }
    }
}
