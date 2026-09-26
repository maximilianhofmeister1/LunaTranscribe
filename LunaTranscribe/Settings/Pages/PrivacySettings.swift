import LunaCore
import SwiftUI

struct PrivacySettings: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared
    @State private var showMCPNotice = false
    @State private var copied: String?
    @State private var checkResult: String?
    @State private var checkFailed = false
    @State private var checking = false
    @State private var activity: [ActivityEntry] = []
    @State private var undoError: String?

    var body: some View {
        SettingsCard(title: "Local-First", systemImage: "lock.shield") {
            ToggleRow(label: "Letzte Diktate im Verlauf zeigen",
                      hint: "Nur im Arbeitsspeicher, nie auf der Platte; weg, sobald die App beendet wird.",
                      isOn: $settings.keepHistory)
            LearnMore(text: "Diktat-Audio bleibt ausschließlich im Arbeitsspeicher und wird direkt nach der Transkription verworfen. Meeting-Aufnahmen liegen während der Verarbeitung temporär in ~/Library/Caches und werden danach gelöscht (außer „Aufnahme behalten“). Transkripte liegen als JSON in deinem Meeting-Ordner. Ausnahmen vom Local-First-Prinzip sind nur die freiwillige Zusammenfassung über die Claude CLI und – wenn du sie freigibst – Meetings, die Claude über luna-mcp liest.")
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

        SettingsCard(title: "Meetings in Claude (MCP)", systemImage: "bubble.left.and.text.bubble.right") {
            ToggleRow(label: "Meetings für Claude freigeben",
                      hint: "Claude Code und Claude Desktop können deine Meetings über luna-mcp durchsuchen und lesen. Vertrauliche Meetings bleiben unsichtbar.",
                      isOn: accessBinding)
            if settings.mcpAccessEnabled {
                ToggleRow(label: "Claude darf Meetings bearbeiten",
                          hint: "Meetings umbenennen und Sprecher benennen. Löschen nur nach deiner Bestätigung in einem Dialog – in den Papierkorb.",
                          isOn: $settings.mcpWriteEnabled)
                if settings.mcpWriteEnabled {
                    ToggleRow(label: "Auch Umbenennungen bestätigen",
                              hint: "Zeigt vor jeder Umbenennung durch Claude einen Dialog.",
                              isOn: $settings.mcpConfirmRenames)
                }
                SettingRow(label: "Claude Code", hint: "Befehl einmal im Terminal ausführen.") {
                    copyButton("Befehl kopieren", id: "code", text: ClaudeConnection.claudeCodeCommand)
                }
                SettingRow(label: "Claude Desktop", hint: "In ~/Library/Application Support/Claude/claude_desktop_config.json einfügen, dann Claude Desktop neu starten.") {
                    copyButton("Eintrag kopieren", id: "desktop", text: ClaudeConnection.claudeDesktopConfig)
                }
                SettingRow(label: "Verbindung testen", hint: "Startet luna-mcp und prüft Meeting-Ordner, Freigaben, Suchindex und Modell.") {
                    Button(checking ? "Prüfe…" : "Testen", action: runCheck)
                        .buttonStyle(.lunaSecondary)
                        .disabled(checking)
                }
                if let checkResult {
                    Text(checkResult)
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundStyle(checkFailed ? LunaColor.rose : LunaColor.inkMuted)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                activitySection
                SettingRow(label: "Suchindex", hint: indexHint) {
                    Button("Löschen") { state.searchIndexer.deleteIndex() }.buttonStyle(.lunaQuiet)
                }
            }
            LearnMore(text: "luna-mcp ist ein kleines Programm in LunaTranscribe, das Claude Code oder Claude Desktop auf deinem Mac starten. Es liest die Meetings direkt aus deinem Meeting-Ordner – ohne Netzwerk. Was Claude liest (Suchtreffer, Transkripte), schickt Claude aber zur Verarbeitung an Anthropic, genau wie bei der Zusammenfassung. Audio und der Diktat-Verlauf sind nie erreichbar. Jeder Zugriff steht im Protokoll unten.")
        }
        .alert("Meetings für Claude freigeben?", isPresented: $showMCPNotice) {
            Button("Freigeben") {
                settings.mcpCloudNoticeAccepted = true
                settings.mcpAccessEnabled = true
            }
            Button("Abbrechen", role: .cancel) {}
        } message: {
            Text("Die Transkription bleibt lokal. Wenn Claude über luna-mcp in deinen Meetings sucht oder sie liest, gehen diese Inhalte über dein Claude-Konto an Anthropic. Als vertraulich markierte Meetings sind ausgenommen. Du kannst die Freigabe jederzeit wieder ausschalten.")
        }
        .task(id: settings.mcpAccessEnabled) { reloadActivity() }

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

    // MARK: Claude

    /// Beim ersten Einschalten erst der Hinweis, was an Anthropic geht.
    private var accessBinding: Binding<Bool> {
        Binding(
            get: { settings.mcpAccessEnabled },
            set: { enabled in
                if enabled && !settings.mcpCloudNoticeAccepted { showMCPNotice = true } else { settings.mcpAccessEnabled = enabled }
            }
        )
    }

    private var indexHint: String {
        guard let stats = state.searchIndexer.stats else {
            return state.searchIndexer.isIndexing ? "Wird aufgebaut …" : "Wird beim nächsten Zugriff aufgebaut."
        }
        let semantic = settings.semanticSearchEnabled ? ", \(stats.embedded) inhaltlich erschlossen" : ""
        return "\(stats.meetings) Meetings, \(stats.chunks) Abschnitte\(semantic). Liegt nur auf diesem Mac; Löschen baut ihn neu auf."
    }

    @ViewBuilder
    private var activitySection: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Zuletzt durch Claude").font(LunaFont.text(13.5))
            if activity.isEmpty {
                Hint("Noch keine Zugriffe.")
            }
            ForEach(Array(activity.enumerated()), id: \.offset) { _, entry in
                HStack(spacing: 8) {
                    Text(entry.date.lunaDateTime).font(LunaFont.caption(11.5)).foregroundStyle(LunaColor.inkMuted)
                    Text(describe(entry)).font(LunaFont.caption(12)).lineLimit(2)
                    Spacer()
                    if entry.action == .renameMeeting || entry.action == .renameSpeaker {
                        Button("Rückgängig") { undo(entry) }.buttonStyle(.lunaQuiet)
                    }
                }
            }
            if let undoError {
                Text(undoError).font(LunaFont.caption(12)).foregroundStyle(LunaColor.rose)
            }
        }
    }

    private func describe(_ entry: ActivityEntry) -> String {
        let titles = entry.meetingIDs.compactMap { state.meetingStore.entry(for: $0)?.title }
        let meeting = titles.first.map { "„\($0)“" } ?? "ein Meeting"
        switch entry.action {
        case .read: return "\(entry.client) hat \(meeting) gelesen"
        case .search: return "\(entry.client) hat gesucht (\(entry.meetingIDs.count) Meeting(s) im Ergebnis)"
        case .renameMeeting: return "Umbenannt: „\(entry.oldValue ?? "")“ → „\(entry.newValue ?? "")“"
        case .renameSpeaker: return "In \(meeting): „\(entry.oldValue ?? "")“ → „\(entry.newValue ?? "")“"
        case .delete: return "\(entry.meetingIDs.count) Meeting(s) in den Papierkorb verschoben (im Finder wiederherstellbar)"
        case .deleteDeclined: return "Löschen abgelehnt"
        }
    }

    private func copyButton(_ title: String, id: String, text: String) -> some View {
        Button(copied == id ? "Kopiert ✓" : title) {
            ClaudeConnection.copy(text)
            copied = id
        }
        .buttonStyle(.lunaSecondary)
    }

    private func runCheck() {
        checking = true
        Task {
            let result = await ClaudeConnection.check()
            checking = false
            switch result {
            case .success(let text): checkResult = text; checkFailed = false
            case .failure(let error): checkResult = error.localizedDescription; checkFailed = true
            }
        }
    }

    private func undo(_ entry: ActivityEntry) {
        do {
            try state.meetingStore.undo(entry)
            undoError = nil
            reloadActivity()
        } catch {
            undoError = error.localizedDescription
        }
    }

    private func reloadActivity() {
        activity = ActivityLog().entries(limit: 8).filter { $0.action != .search }
    }

    // MARK: Berechtigungen

    @ViewBuilder
    private func permissionState(_ granted: Bool, open: @escaping () -> Void) -> some View {
        if granted {
            Label("Erteilt", systemImage: "checkmark.circle.fill").foregroundStyle(LunaColor.sage)
        } else {
            Button("Erlauben…", action: open).buttonStyle(.lunaPrimary)
        }
    }
}
