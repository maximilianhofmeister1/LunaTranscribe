import Combine
import Foundation
import LunaCore

/// Erstellt Meeting-Zusammenfassungen über die eingestellte CLI (`SummaryProvider`: Claude, Antigravity
/// oder Codex). Läuft je Meeting höchstens einmal
/// gleichzeitig und schreibt das Ergebnis selbst in die Bibliothek – auch wenn das Meeting
/// inzwischen nicht mehr angezeigt wird.
@MainActor
final class MeetingSummarizer: ObservableObject {
    /// Meetings, für die gerade eine Zusammenfassung läuft, mit dem Anbieter dieses Laufs.
    @Published private(set) var running: [UUID: SummaryProvider] = [:]
    /// Letzter Fehler je Meeting (bis zum nächsten Versuch).
    @Published private(set) var errors: [UUID: String] = [:]

    /// Meldet eine fertige Zusammenfassung, damit ein geöffnetes Meeting sie übernimmt.
    let completed = PassthroughSubject<(UUID, MeetingSummary), Never>()

    private let settings: SettingsStore
    private let store: MeetingStore
    private var tasks: [UUID: Task<Void, Never>] = [:]

    init(settings: SettingsStore, store: MeetingStore) {
        self.settings = settings
        self.store = store
    }

    func isRunning(_ id: UUID) -> Bool { running[id] != nil }

    /// Fasst `transcript` in `language` zusammen (Wahl im Meeting, vorbelegt aus `SettingsStore.summaryLanguage`).
    /// Ersetzt eine vorhandene – auch bearbeitete – Zusammenfassung; das Nachfragen ist Sache der Oberfläche.
    func summarize(_ transcript: MeetingTranscript, language: AppLanguage) {
        let id = transcript.id
        guard running[id] == nil else { return }
        errors[id] = nil
        // Vertraulich heißt: kein Text in die Cloud – weder über luna-mcp noch über die Zusammenfassung.
        guard !transcript.confidential else {
            errors[id] = "Dieses Meeting ist als vertraulich markiert und wird nicht in der Cloud zusammengefasst."
            return
        }

        let provider = settings.summaryProvider
        let customPath = settings.cliPath(for: provider)
        let instructions = SummaryPrompt.instructions(template: settings.summaryPrompt, language: language)
        let input = transcript.render(as: .text)
        let model = settings.model(for: provider)

        running[id] = provider
        tasks[id] = Task { [weak self] in
            let started = Date()
            do {
                // `locateCLI` fragt im Zweifel die Login-Shell – nicht auf dem Main Thread.
                guard let cli = await Task.detached(operation: { provider.locateCLI(customPath: customPath) }).value else {
                    throw CLIError.notFound(provider)
                }
                Log.summary.notice("summary started: \(transcript.wordCount) words in \(language.rawValue, privacy: .public) via \(cli.executable.path, privacy: .public)")
                let markdown = try await cli.complete(instructions: instructions, input: input,
                                                      model: model.isEmpty ? nil : model, timeout: 300)
                let summary = MeetingSummary(markdown: markdown, createdAt: Date(), model: model.isEmpty ? nil : model,
                                             language: language, provider: provider)
                self?.finish(id, with: summary)
                Log.summary.notice("summary done in \(Date().timeIntervalSince(started), format: .fixed(precision: 1)) s")
            } catch is CancellationError {
                self?.running[id] = nil
            } catch {
                self?.running[id] = nil
                self?.errors[id] = error.localizedDescription
                Log.summary.error("summary failed: \(error.localizedDescription, privacy: .public)")
            }
            self?.tasks[id] = nil
        }
    }

    func cancel(_ id: UUID) {
        tasks[id]?.cancel()
        tasks[id] = nil
        running[id] = nil
    }

    /// Prüft die CLI eines Anbieters für die Einstellungen: Pfad und Version, oder die Fehlermeldung.
    func checkCLI(_ provider: SummaryProvider) async -> Result<String, Error> {
        let customPath = settings.cliPath(for: provider)
        guard let cli = await Task.detached(operation: { provider.locateCLI(customPath: customPath) }).value else {
            return .failure(CLIError.notFound(provider))
        }
        do {
            let version = try await cli.version()
            return .success("\(cli.executable.path) · \(version)")
        } catch {
            return .failure(error)
        }
    }

    /// Frischen Stand von der Platte lesen, Zusammenfassung setzen, speichern, geöffnete Ansicht informieren.
    private func finish(_ id: UUID, with summary: MeetingSummary) {
        running[id] = nil
        do {
            var latest = try store.load(id)
            latest.summary = summary
            try store.update(latest)
        } catch MeetingStore.StoreError.notFound {
            // Inzwischen gelöscht (z. B. von Claude über luna-mcp) – das Ergebnis hat kein Ziel mehr.
            Log.summary.notice("summary discarded: meeting no longer exists")
        } catch {
            errors[id] = "Zusammenfassung erstellt, aber nicht gespeichert: \(error.localizedDescription)"
        }
        completed.send((id, summary))
    }
}
