import SwiftUI
import KeyboardShortcuts

struct SettingsView: View {
    @ObservedObject var settings = SettingsStore.shared
    @EnvironmentObject var state: AppState
    @State private var devices: [AudioInputDevice] = AudioDevices.inputDevices()

    var body: some View {
        Form {
            Section("Allgemein") {
                Toggle("Beim Anmelden automatisch starten", isOn: $settings.launchAtLogin)
                if let err = settings.launchAtLoginError {
                    Text(err).font(.caption).foregroundStyle(.red)
                }
                Text("Funktioniert zuverlässig, wenn die App in /Programme liegt (siehe README).")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Tastenkürzel") {
                KeyboardShortcuts.Recorder("Diktieren (Hauptsprache):", name: .recordPrimary)
                Picker("Hauptsprache", selection: $settings.primaryLanguage) {
                    ForEach(AppLanguage.supported(by: settings.engine)) { Text($0.displayName).tag($0) }
                }

                KeyboardShortcuts.Recorder("Diktieren (Zweitsprache):", name: .recordSecondary)
                Picker("Zweitsprache", selection: $settings.secondaryLanguage) {
                    ForEach(AppLanguage.supported(by: settings.engine)) { Text($0.displayName).tag($0) }
                }

                Picker("Verhalten", selection: $settings.recordingMode) {
                    ForEach(RecordingMode.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.radioGroup)

                Text("Tipp: Damit F2 ohne fn-Taste funktioniert, in Systemeinstellungen → Tastatur → „F1, F2 usw. als Standard-Funktionstasten“ aktivieren.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Audio") {
                Picker("Mikrofon", selection: $settings.inputDeviceUID) {
                    Text("Systemstandard" + defaultDeviceSuffix).tag("")
                    ForEach(devices) { Text($0.name).tag($0.uid) }
                }
                Button("Geräte aktualisieren") { devices = AudioDevices.inputDevices() }
                Toggle("Töne bei Start/Stopp", isOn: $settings.playSounds)
                Toggle("Wiedergabe (Musik, Videos) während der Aufnahme pausieren", isOn: $settings.pauseMedia)
            }

            Section("Meeting") {
                Toggle("Systemaudio (Zoom, Teams, Browser) zusätzlich zum Mikrofon aufnehmen", isOn: $settings.meetingCaptureSystemAudio)
                Toggle("Aufnahme (WAV) nach der Verarbeitung behalten", isOn: $settings.meetingKeepAudio)
                LabeledContent("Ablageordner") {
                    HStack {
                        Text(MeetingExporter.folder(from: settings.meetingExportFolder).path)
                            .lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
                        Button("Wählen…") { chooseMeetingFolder() }
                        if !settings.meetingExportFolder.isEmpty {
                            Button("Standard") { settings.meetingExportFolder = "" }
                        }
                    }
                }
                Text("Während des Meetings wird nur aufgenommen; Sprecher-Erkennung und Transkription laufen nach „Beenden“. Für Online-Meetings Kopfhörer nutzen, sonst hört das Mikrofon die Lautsprecher mit. Beim ersten Meeting fragt macOS nach der Berechtigung „Systemaudio aufnehmen“.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Modell") {
                Picker("Engine", selection: $settings.engine) {
                    ForEach(EngineKind.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.radioGroup)
                modelStatus
                Text("Modelle liegen in ~/Library/Application Support/LunaTranscribe/Models. Einmaliger Download von Hugging Face; danach arbeitet die App komplett offline.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Datenschutz") {
                Toggle("Letzte Diktate im Menü anzeigen (nur im RAM, nie gespeichert)", isOn: $settings.keepHistory)
                Text("Diktate bleiben ausschließlich im Arbeitsspeicher und werden direkt nach der Transkription verworfen. Meeting-Aufnahmen liegen während der Verarbeitung temporär in ~/Library/Caches und werden danach gelöscht (außer „Aufnahme behalten“). Es werden keine Daten an Server gesendet.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section("Berechtigungen") {
                LabeledContent("Mikrofon") {
                    Text(Permissions.microphoneStatus == .authorized ? "Erteilt" : "Fehlt")
                }
                LabeledContent("Bedienungshilfen") {
                    HStack {
                        Text(state.accessibilityGranted ? "Erteilt" : "Fehlt")
                        if !state.accessibilityGranted {
                            Button("Öffnen…") { Permissions.openAccessibilitySettings() }
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        // Fenster frei skalierbar; Form scrollt bei Bedarf
        .frame(minWidth: 480, idealWidth: 560, maxWidth: .infinity,
               minHeight: 400, idealHeight: 760, maxHeight: .infinity)
        .onChange(of: settings.engine) { _, newEngine in
            // Sprachen ggf. auf etwas Unterstütztes zurücksetzen
            if !settings.primaryLanguage.isSupported(by: newEngine) { settings.primaryLanguage = .english }
            if !settings.secondaryLanguage.isSupported(by: newEngine) { settings.secondaryLanguage = .english }
        }
    }

    private func chooseMeetingFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.directoryURL = MeetingExporter.folder(from: settings.meetingExportFolder)
        if panel.runModal() == .OK, let url = panel.url {
            settings.meetingExportFolder = url.path
        }
    }

    private var defaultDeviceSuffix: String {
        if let d = AudioDevices.defaultInputDevice() { return " (\(d.name))" }
        return ""
    }

    @ViewBuilder
    private var modelStatus: some View {
        switch state.models.state {
        case .loading(let p):
            HStack {
                ProgressView(value: p)
                Text(state.models.statusText).font(.caption)
            }
        case .ready(let kind):
            Label("\(kind.shortLabel) bereit", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
        case .failed(let msg):
            VStack(alignment: .leading) {
                Label(msg, systemImage: "xmark.circle.fill").foregroundStyle(.red)
                Button("Erneut versuchen") { state.models.retry(settings.engine) }
            }
        case .idle:
            Text("Kein Modell geladen")
        }
    }
}
