import SwiftUI

struct PrivacySettings: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared

    var body: some View {
        SettingsCard(title: "Local-First", systemImage: "lock.shield") {
            ToggleRow(label: "Letzte Diktate im Verlauf zeigen",
                      hint: "Nur im Arbeitsspeicher, nie auf der Platte; weg, sobald die App beendet wird.",
                      isOn: $settings.keepHistory)
            LearnMore(text: "Diktat-Audio bleibt ausschließlich im Arbeitsspeicher und wird direkt nach der Transkription verworfen. Meeting-Aufnahmen liegen während der Verarbeitung temporär in ~/Library/Caches und werden danach gelöscht (außer „Aufnahme behalten“). Transkripte liegen als JSON in deinem Meeting-Ordner. Die einzige Ausnahme vom Local-First-Prinzip ist die freiwillige Zusammenfassung über die Claude CLI.")
            if !settings.ignoredSuggestionKeys.isEmpty {
                SettingRow(label: "Ignorierte Wörterbuch-Vorschläge", hint: "\(settings.ignoredSuggestionKeys.count) Korrekturen werden nicht mehr vorgeschlagen.") {
                    Button("Zurücksetzen") { settings.ignoredSuggestionKeys = [] }.buttonStyle(.lunaSecondary)
                }
            }
            if settings.summaryCloudNoticeAccepted {
                SettingRow(label: "Cloud-Hinweis für Zusammenfassungen", hint: "Wurde bestätigt.") {
                    Button("Wieder anzeigen") { settings.summaryCloudNoticeAccepted = false }.buttonStyle(.lunaSecondary)
                }
            }
        }

        SettingsCard(title: "Berechtigungen", systemImage: "checkmark.shield") {
            SettingRow(label: "Mikrofon") {
                permissionState(Permissions.microphoneStatus == .authorized) { Permissions.openMicrophoneSettings() }
            }
            SettingRow(label: "Bedienungshilfen", hint: "Nötig, um Text per ⌘V in andere Apps einzufügen.") {
                permissionState(state.accessibilityGranted) { Permissions.openAccessibilitySettings() }
            }
            SettingRow(label: "Systemaudio", hint: "Wird beim ersten Meeting mit Systemaudio abgefragt.") {
                Button("Öffnen…") { Permissions.openSystemAudioSettings() }.buttonStyle(.lunaSecondary)
            }
        }
    }

    @ViewBuilder
    private func permissionState(_ granted: Bool, open: @escaping () -> Void) -> some View {
        if granted {
            Label("Erteilt", systemImage: "checkmark.circle.fill").foregroundStyle(LunaColor.sage)
        } else {
            Button("Erlauben…", action: open).buttonStyle(.lunaPrimary)
        }
    }
}
