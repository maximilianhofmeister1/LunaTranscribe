import Combine
import Foundation

/// Ein im Hauptfenster geöffnetes Meeting. Änderungen (Titel, Sprecher, Text) werden kurz verzögert
/// automatisch gespeichert; eine fertige Zusammenfassung wird übernommen, ohne ungespeicherte
/// Änderungen zu verlieren. Korrigierte Beiträge speisen das lernende Wörterbuch.
@MainActor
final class MeetingDocument: ObservableObject {
    @Published var transcript: MeetingTranscript {
        didSet { scheduleSave() }
    }
    @Published private(set) var saveError: String?
    /// Zahl der Wörterbuch-Vorschläge aus der letzten Korrektur (für einen kurzen Hinweis).
    @Published private(set) var learnedSuggestions = 0

    private let store: MeetingStore
    private let learner: VocabularyLearner
    private var saveTask: Task<Void, Never>?
    private var cancellables = Set<AnyCancellable>()

    static let saveDelay: Duration = .milliseconds(500)

    init(transcript: MeetingTranscript, store: MeetingStore, learner: VocabularyLearner, summarizer: MeetingSummarizer) {
        self.transcript = transcript
        self.store = store
        self.learner = learner
        summarizer.completed
            .filter { $0.0 == transcript.id }
            .sink { [weak self] _, summary in self?.transcript.summary = summary }
            .store(in: &cancellables)
    }

    /// Ersetzt den Text eines Beitrags und lernt aus der Korrektur.
    func correctTurn(_ id: MeetingTranscript.Turn.ID, to newText: String) {
        guard let index = transcript.turns.firstIndex(where: { $0.id == id }) else { return }
        let old = transcript.turns[index].text
        let cleaned = newText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard cleaned != old, !cleaned.isEmpty else { return }
        transcript.turns[index].text = cleaned
        learnedSuggestions = learner.learn(before: old, after: cleaned)
    }

    /// Sofort speichern (z. B. beim Schließen oder Wechseln).
    func flush() {
        guard saveTask != nil else { return }
        saveTask?.cancel()
        saveTask = nil
        save()
    }

    private func scheduleSave() {
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: Self.saveDelay)
            guard !Task.isCancelled else { return }
            self?.saveTask = nil
            self?.save()
        }
    }

    private func save() {
        do {
            try store.update(transcript)
            saveError = nil
        } catch {
            saveError = "Speichern fehlgeschlagen: \(error.localizedDescription)"
        }
    }
}
