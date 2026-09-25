import KeyboardShortcuts
import SwiftUI

struct DictationSettings: View {
    @ObservedObject private var settings = SettingsStore.shared
    @State private var devices: [AudioInputDevice] = AudioDevices.inputDevices()

    var body: some View {
        SettingsCard(title: "Tastenkürzel & Sprachen", systemImage: "keyboard") {
            SettingRow(label: "Diktieren") {
                KeyboardShortcuts.Recorder(for: .recordPrimary)
                languagePicker($settings.primaryLanguage)
            }
            SettingRow(label: "Diktieren in Zweitsprache") {
                KeyboardShortcuts.Recorder(for: .recordSecondary)
                languagePicker($settings.secondaryLanguage)
            }
            SettingRow(label: "Diktieren + Nachbearbeitung", hint: "Hauptsprache; ein lokales Sprachmodell bereinigt den Text.") {
                KeyboardShortcuts.Recorder(for: .recordRefined)
            }
            SettingRow(label: "Verhalten") {
                Picker("", selection: $settings.recordingMode) {
                    ForEach(RecordingMode.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }
            LearnMore(text: "Damit F2 ohne fn-Taste funktioniert: Systemeinstellungen → Tastatur → Tastaturkurzbefehle → Funktionstasten → „F1, F2 usw. als Standard-Funktionstasten“. Escape bricht ein laufendes Diktat ab.")
        }

        SettingsCard(title: "Audio", systemImage: "mic") {
            SettingRow(label: "Mikrofon") {
                Picker("", selection: $settings.inputDeviceUID) {
                    Text("Systemstandard" + defaultDeviceSuffix).tag("")
                    ForEach(devices) { Text($0.name).tag($0.uid) }
                    // Gewähltes Gerät gerade nicht angeschlossen: sichtbar machen statt leerer Auswahl.
                    if !settings.inputDeviceUID.isEmpty, !devices.contains(where: { $0.uid == settings.inputDeviceUID }) {
                        Text("Gewähltes Mikrofon (nicht verbunden)").tag(settings.inputDeviceUID)
                    }
                }
                .labelsHidden()
                .frame(maxWidth: 260)
                Button { devices = AudioDevices.inputDevices() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.lunaQuiet)
                    .help("Geräte aktualisieren")
            }
            ToggleRow(label: "Musik und Videos während der Aufnahme pausieren",
                      hint: "Setzt nur fort, was LunaTranscribe selbst pausiert hat.",
                      isOn: $settings.pauseMedia)
        }
    }

    private func languagePicker(_ selection: Binding<AppLanguage>) -> some View {
        Picker("", selection: selection) {
            ForEach(AppLanguage.supported(by: settings.engine)) { Text($0.displayName).tag($0) }
        }
        .labelsHidden()
        .frame(width: 150)
    }

    private var defaultDeviceSuffix: String {
        AudioDevices.defaultInputDevice().map { " (\($0.name))" } ?? ""
    }
}
