import SwiftUI

struct ModelSettings: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared

    var body: some View {
        SettingsCard(title: "Spracherkennung", systemImage: "waveform.badge.mic") {
            Picker("", selection: $settings.engine) {
                ForEach(EngineKind.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            LoadStatusView(status: state.models.loadStatus) { state.models.retry(settings.engine) }
            SettingRow(label: "Modellordner", hint: AppPaths.models.path) {
                Button("Im Finder zeigen") {
                    try? FileManager.default.createDirectory(at: AppPaths.models, withIntermediateDirectories: true)
                    NSWorkspace.shared.open(AppPaths.models)
                }
                .buttonStyle(.lunaSecondary)
            }
            LearnMore(text: "Modelle (Transkription, Sprecher, Nachbearbeitung) werden einmalig von Hugging Face geladen; danach arbeitet die App komplett offline. Parakeet unterstützt 25 europäische Sprachen, Cohere 14 Sprachen inklusive Chinesisch, Japanisch, Koreanisch und Arabisch.")
        }
        .onChange(of: settings.engine) { _, engine in
            // Sprachen ggf. auf etwas Unterstütztes zurücksetzen
            if !settings.primaryLanguage.isSupported(by: engine) { settings.primaryLanguage = .english }
            if !settings.secondaryLanguage.isSupported(by: engine) { settings.secondaryLanguage = .english }
        }
    }
}
