import SwiftUI

struct GeneralSettings: View {
    @ObservedObject private var settings = SettingsStore.shared

    var body: some View {
        SettingsCard(title: "Start & Klang", systemImage: "power") {
            ToggleRow(label: "Beim Anmelden automatisch starten",
                      hint: "Funktioniert zuverlässig, wenn die App in /Programme liegt.",
                      isOn: $settings.launchAtLogin)
            if let error = settings.launchAtLoginError {
                Text(error).font(LunaFont.caption(12)).foregroundStyle(LunaColor.rose)
            }
            ToggleRow(label: "Töne bei Start und Stopp", isOn: $settings.playSounds)
        }
    }
}
