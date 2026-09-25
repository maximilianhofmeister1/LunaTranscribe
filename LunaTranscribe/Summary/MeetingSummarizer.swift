import Combine
import Foundation
import LunaCore

/// Erstellt Meeting-Zusammenfassungen über die Claude CLI. Läuft je Meeting höchstens einmal
/// gleichzeitig und schreibt das Ergebnis selbst in die Bibliothek – auch wenn das Meeting
/// inzwischen nicht mehr angezeigt wird.
@MainActor
final class MeetingSummarizer: ObservableObject {
    /// Meetings, für die gerade eine Zusammenfassung läuft.
    @Published private(set) var running: Set<UUID> = []
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

    func isRunning(_ id: UUID) -> Bool { running.contains(id) }

    func summarize(_ transcript: MeetingTranscript) {
        let id = transcript.id
        guard !running.contains(id) else { return }
        errors[id] = nil
        // Vertraulich heißt: kein Text an Anthropic – weder über luna-mcp noch über die Zusammenfassung.
        guard !transcript.confidential else {
            errors[id] = "Dieses Meeting ist als vertraulich markiert und wird nicht an Claude geschickt."
            return
        }

        guard let cli = ClaudeCLI.locate(customPath: settings.claudeCLIPath) else {
            errors[id] = ClaudeCLI.CLIError.notFound.localizedDescription
            return
        }
        let instructions = SummaryPrompt.instructions(template: settings.summaryPrompt, language: transcript.language)
        let input = transcript.render(as: .text)
        let model = settings.claudeModel.trimmingCharacters(in: .whitespaces)

        running.insert(id)
        Log.summary.notice("summary started: \(transcript.wordCount) words via \(cli.executable.path, privacy: .public)")
        tasks[id] = Task { [weak self] in
            let started = Date()
            do {
                let markdown = try await cli.complete(instructions: instructions, input: input, model: model.isEmpty ? nil : model)
                let summary = MeetingSummary(markdown: markdown, createdAt: Date(), model: model.isEmpty ? nil : model)
                self?.finish(id, with: summary)
                Log.summary.notice("summary done in \(Date().timeIntervalSince(started), format: .fixed(precision: 1)) s")
            } catch is CancellationError {
                self?.running.remove(id)
            } catch {
                self?.running.remove(id)
                self?.errors[id] = error.localizedDescription
                Log.summary.error("summary failed: \(error.localizedDescription, privacy: .public)")
            }
            self?.tasks[id] = nil
        }
    }

    func cancel(_ id: UUID) {
        tasks[id]?.cancel()
        tasks[id] = nil
        running.remove(id)
    }

    /// Prüft die CLI für die Einstellungen: Pfad und Version, oder die Fehlermeldung.
    func checkCLI() async -> Result<String, Error> {
        guard let cli = ClaudeCLI.locate(customPath: settings.claudeCLIPath) else { return .failure(ClaudeCLI.CLIError.notFound) }
        do {
            let version = try await cli.version()
            return .success("\(cli.executable.path) · \(version)")
        } catch {
            return .failure(error)
        }
    }

    /// Frischen Stand von der Platte lesen, Zusammenfassung setzen, speichern, geöffnete Ansicht informieren.
    private func finish(_ id: UUID, with summary: MeetingSummary) {
        running.remove(id)
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
