import SwiftUI

/// Einstellungen im Hauptfenster: Bereichs-Tabs oben, darunter eine ruhige Seite je Bereich.
struct SettingsPage: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var router: MainRouter

    var body: some View {
        LunaPage(title: "Einstellungen") {
            HStack(spacing: 6) {
                ForEach(SettingsSection.allCases) { section in
                    Button(section.title) { router.settingsSection = section }
                        .buttonStyle(LunaButtonStyle(kind: router.settingsSection == section ? .primary : .quiet))
                }
            }
            .padding(4)
            .background(LunaColor.surfaceMuted, in: Capsule())

            switch router.settingsSection {
            case .general: GeneralSettings()
            case .dictation: DictationSettings()
            case .refine: RefineSettings()
            case .meetings: MeetingSettings()
            case .models: ModelSettings()
            case .privacy: PrivacySettings()
            }
        }
    }
}

// MARK: Bausteine

/// Karte mit Titel für eine Gruppe von Einstellungen.
struct SettingsCard<Content: View>: View {
    let title: String
    var systemImage: String?
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            SectionTitle(title: title, systemImage: systemImage)
            content()
        }
        .lunaCard(padding: 18)
    }
}

/// Eine Einstellung: Beschriftung (optional mit Hinweis) links, Bedienelement rechts.
struct SettingRow<Control: View>: View {
    let label: String
    var hint: String?
    @ViewBuilder var control: () -> Control

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(LunaFont.text(13.5))
                if let hint { Hint(hint) }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            control()
        }
    }
}

/// Schalter als Zeile.
struct ToggleRow: View {
    let label: String
    var hint: String?
    @Binding var isOn: Bool

    var body: some View {
        SettingRow(label: label, hint: hint) {
            Toggle("", isOn: $isOn).toggleStyle(.switch).labelsHidden()
        }
    }
}

/// Mehrzeiliger Prompt-Editor mit „Auf Standard zurücksetzen“.
struct PromptEditor: View {
    @Binding var text: String
    let isDefault: Bool
    let onReset: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            TextEditor(text: $text)
                .font(.system(size: 12, design: .monospaced))
                .scrollContentBackground(.hidden)
                .padding(8)
                .frame(minHeight: 160, maxHeight: 320)
                .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(LunaColor.stroke))
            if !isDefault {
                Button("Auf Standard zurücksetzen", action: onReset).buttonStyle(.lunaQuiet)
            }
        }
    }
}
