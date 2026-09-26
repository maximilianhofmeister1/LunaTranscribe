import Combine
import Foundation
import LunaCore

/// Ein im Hauptfenster geöffnetes Meeting. Änderungen (Titel, Sprecher, Text) werden kurz verzögert
/// automatisch gespeichert; eine fertige Zusammenfassung wird übernommen, ohne ungespeicherte
/// Änderungen zu verlieren. Korrigierte Beiträge speisen das lernende Wörterbuch.
///
/// Claude kann dasselbe Meeting über luna-mcp umbenennen oder löschen. Ohne eigene Änderungen übernimmt
/// das Dokument den neuen Stand sofort; mit ungespeicherten Änderungen führt `MeetingStore.save` beide
/// Seiten zusammen (was der Nutzer geändert hat, gewinnt). Ein gelöschtes Meeting wird nie neu angelegt.
@MainActor
final class MeetingDocument: ObservableObject {
    @Published var transcript: MeetingTranscript {
        didSet { if !applyingStoredState { scheduleSave() } }
    }
    @Published private(set) var saveError: String?
    /// Zahl der Wörterbuch-Vorschläge aus der letzten Korrektur (für einen kurzen Hinweis).
    @Published private(set) var learnedSuggestions = 0
    /// Das Meeting ist nicht mehr in der Bibliothek (von Claude oder im Finder gelöscht).
    @Published private(set) var isDeleted = false

    private let store: MeetingStore
    private let learner: VocabularyLearner
    /// Stand der Platte beim Laden bzw. letzten Speichern – Basis für die Zusammenführung.
    private var base: MeetingTranscript
    private var applyingStoredState = false
    private var saveTask: Task<Void, Never>?
    private var cancellables = Set<AnyCancellable>()

    static let saveDelay: Duration = .milliseconds(500)

    init(transcript: MeetingTranscript, store: MeetingStore, learner: VocabularyLearner, summarizer: MeetingSummarizer) {
        self.transcript = transcript
        self.base = transcript
        self.store = store
        self.learner = learner
        summarizer.completed
            .filter { $0.0 == transcript.id }
            .sink { [weak self] _, summary in self?.transcript.summary = summary }
            .store(in: &cancellables)
        store.externalChanges
            .filter { $0.contains(transcript.id) }
            .sink { [weak self] _ in self?.adoptExternalChanges() }
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
        guard !isDeleted else { return }
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: Self.saveDelay)
            guard !Task.isCancelled else { return }
            self?.saveTask = nil
            self?.save()
        }
    }

    private func save() {
        guard !isDeleted else { return }
        do {
            let saved = try store.save(transcript, base: base)
            base = saved
            if saved != transcript { applyStoredState(saved) }
            saveError = nil
        } catch MeetingStore.StoreError.notFound {
            markDeleted()
        } catch MeetingLibrary.LibraryError.notFound {
            markDeleted()
        } catch {
            saveError = "Speichern fehlgeschlagen: \(error.localizedDescription)"
        }
    }

    /// Claude hat das Meeting geändert oder gelöscht. Mit ausstehenden eigenen Änderungen übernimmt
    /// das nächste Speichern (Zusammenführung) den neuen Stand; sonst sofort neu laden.
    private func adoptExternalChanges() {
        guard saveTask == nil else { return }
        do {
            let fresh = try store.load(transcript.id)
            base = fresh
            applyStoredState(fresh)
        } catch {
            markDeleted()
        }
    }

    private func markDeleted() {
        saveTask?.cancel()
        saveTask = nil
        isDeleted = true
        saveError = "Dieses Meeting ist nicht mehr in der Bibliothek – es wurde gelöscht (Papierkorb) oder im Finder verschoben."
    }

    /// Übernimmt einen gespeicherten Stand, ohne erneut zu speichern.
    private func applyStoredState(_ stored: MeetingTranscript) {
        applyingStoredState = true
        transcript = stored
        applyingStoredState = false
    }
}
