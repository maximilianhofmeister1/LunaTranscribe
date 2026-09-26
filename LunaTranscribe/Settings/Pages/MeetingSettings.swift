import AppKit
import LunaCore
import SwiftUI

struct MeetingSettings: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared
    @State private var cliCheck: String?
    @State private var cliCheckFailed = false
    @State private var checking = false
    @State private var folderError: String?

    var body: some View {
        SettingsCard(title: "Meeting-Ordner", systemImage: "folder") {
            SettingRow(label: settings.meetingFolderURL.path,
                       hint: "Hier liegt pro Meeting ein Ordner mit meeting.json, summary.md und – falls behalten – der Aufnahme.") {
                Button("Wählen…", action: chooseFolder).buttonStyle(.lunaSecondary)
                if !settings.meetingFolder.isEmpty {
                    Button("Standard") { changeFolder(to: "") }.buttonStyle(.lunaQuiet)
                }
            }
            if let folderError {
                Text(folderError).font(LunaFont.caption(12)).foregroundStyle(LunaColor.rose)
            }
            ToggleRow(label: "Aufnahme (WAV) im Meeting-Ordner behalten",
                      hint: "Sonst wird die Aufnahme nach der Verarbeitung gelöscht.",
                      isOn: $settings.meetingKeepAudio)
        }

        SettingsCard(title: "Aufnahme", systemImage: "waveform") {
            ToggleRow(label: "Systemaudio (Zoom, Teams, Browser) zusätzlich zum Mikrofon aufnehmen",
                      isOn: $settings.meetingCaptureSystemAudio)
            LearnMore(text: "Während des Meetings wird nur aufgenommen; Sprecher-Erkennung und Transkription laufen lokal nach „Beenden“. Für Online-Meetings Kopfhörer nutzen, sonst hört das Mikrofon die Lautsprecher mit. Beim ersten Meeting fragt macOS nach der Berechtigung „Systemaudio aufnehmen“.")
        }

        SettingsCard(title: "Inhaltliche Suche", systemImage: "text.magnifyingglass") {
            ToggleRow(label: "Meetings nach Bedeutung durchsuchbar machen",
                      hint: "Claude findet dann auch Umschreibungen („teurer werden“ zu „Preiserhöhung“). Lädt einmalig \(EmbeddingModelKind.standard.label); alles bleibt auf diesem Mac.",
                      isOn: $settings.semanticSearchEnabled)
            if settings.semanticSearchEnabled {
                semanticSearchStatus
            }
            if !settings.mcpAccessEnabled {
                Hint("Wirkt, sobald Meetings für Claude freigegeben sind (Einstellungen → Datenschutz).")
            }
        }

        SettingsCard(title: "Zusammenfassung mit Claude", systemImage: "sparkles") {
            Label("Die Transkription bleibt lokal. Nur wenn du bei einem Meeting „Zusammenfassen“ klickst, geht dessen Text über die Claude CLI an Anthropic.",
                  systemImage: "cloud")
                .font(LunaFont.caption(12))
                .foregroundStyle(LunaColor.inkMuted)
                .fixedSize(horizontal: false, vertical: true)

            SettingRow(label: "Claude CLI", hint: "Leer = automatisch suchen (~/.local/bin, Homebrew, Login-Shell).") {
                TextField("automatisch", text: $settings.claudeCLIPath)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 220)
                Button(checking ? "Prüfe…" : "Testen", action: checkCLI)
                    .buttonStyle(.lunaSecondary)
                    .disabled(checking)
            }
            if let cliCheck {
                Label(cliCheck, systemImage: cliCheckFailed ? "xmark.circle.fill" : "checkmark.circle.fill")
                    .font(LunaFont.caption(12))
                    .foregroundStyle(cliCheckFailed ? LunaColor.rose : LunaColor.sage)
                    .fixedSize(horizontal: false, vertical: true)
            }
            SettingRow(label: "Modell", hint: "Alias wie „sonnet“ oder „opus“; leer = Standard der CLI.") {
                TextField("Standard", text: $settings.claudeModel)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 220)
            }
            VStack(alignment: .leading, spacing: 6) {
                Text("Anweisung für die Zusammenfassung").font(LunaFont.text(13.5))
                PromptEditor(text: $settings.summaryPrompt, isDefault: settings.isSummaryPromptDefault,
                             onReset: settings.resetSummaryPrompt)
                Hint("„{language}“ wird durch die Sprache des Meetings ersetzt. Das Transkript hängt die App mit Zeitstempeln und Sprechernamen an.")
            }
        }
    }

    @ViewBuilder
    private var semanticSearchStatus: some View {
        switch state.searchIndexer.modelState {
        case .off, .ready:
            let stats = state.searchIndexer.stats
            Label(stats.map { "Modell bereit · \($0.embedded) von \($0.chunks) Abschnitten erschlossen" } ?? "Modell bereit",
                  systemImage: "checkmark.circle.fill")
                .font(LunaFont.caption(12)).foregroundStyle(LunaColor.sage)
        case .notDownloaded:
            SettingRow(label: "Modell noch nicht geladen") {
                Button("Laden") { state.searchIndexer.downloadModel() }.buttonStyle(.lunaSecondary)
            }
        case .downloading(let fraction):
            ProgressView(value: fraction) { Text("Lade Modell …").font(LunaFont.caption(12)) }
        case .failed(let message):
            SettingRow(label: message) {
                Button("Erneut versuchen") { state.searchIndexer.downloadModel() }.buttonStyle(.lunaSecondary)
            }
        }
    }

    // MARK: Aktionen

    private func checkCLI() {
        checking = true
        Task {
            let result = await state.summarizer.checkCLI()
            checking = false
            switch result {
            case .success(let info): cliCheck = "Gefunden: \(info)"; cliCheckFailed = false
            case .failure(let error): cliCheck = error.localizedDescription; cliCheckFailed = true
            }
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Als Meeting-Ordner verwenden"
        panel.directoryURL = settings.meetingFolderURL
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK, let url = panel.url { changeFolder(to: url.path) }
    }

    /// Wechselt den Meeting-Ordner und fragt, ob vorhandene Meetings mitziehen sollen.
    private func changeFolder(to newPath: String) {
        folderError = nil
        let oldFolder = settings.meetingFolderURL
        guard AppPaths.meetingFolder(from: newPath).standardizedFileURL != oldFolder.standardizedFileURL else { return }

        var moveExisting = false
        if !state.meetingStore.entries.filter({ !$0.isUnsaved }).isEmpty {
            let alert = NSAlert()
            alert.messageText = "Vorhandene Meetings verschieben?"
            alert.informativeText = "Im bisherigen Ordner liegen \(state.meetingStore.entries.count) Meetings. Sollen sie in den neuen Ordner umziehen? Sonst bleiben sie dort und erscheinen erst wieder, wenn du zurückwechselst."
            alert.addButton(withTitle: "Verschieben")
            alert.addButton(withTitle: "Nur neuen Ordner nutzen")
            alert.addButton(withTitle: "Abbrechen")
            switch alert.runModal() {
            case .alertFirstButtonReturn: moveExisting = true
            case .alertSecondButtonReturn: moveExisting = false
            default: return
            }
        }

        settings.meetingFolder = newPath
        if moveExisting {
            do {
                try state.meetingStore.moveMeetings(from: oldFolder)
            } catch {
                folderError = "Verschieben fehlgeschlagen: \(error.localizedDescription)"
            }
        }
    }
}
