import Foundation
import Combine
import LunaCore

/// Hält genau eine geladene Engine im Speicher und wechselt sie bei Bedarf.
@MainActor
final class ModelManager: ObservableObject {
    enum State: Equatable {
        case idle
        case loading(Double)
        case ready(EngineKind)
        case failed(String)
    }

    /// Zustand des eigenen Wörterbuchs der geladenen Engine.
    enum VocabularyState: Equatable {
        case off
        /// Wörterbuch wird übernommen bzw. das Boosting-Modell geladen.
        case loading
        /// `boosted`: akustisches Boosting läuft zusätzlich zur Schreibweisen-Korrektur.
        case ready(terms: Int, boosted: Bool)
        /// Boosting ist fehlgeschlagen; die Schreibweisen-Korrektur läuft trotzdem.
        case failed(String)
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var vocabularyState: VocabularyState = .off

    /// Aktuelles Wörterbuch; jede neu geladene Engine bekommt es nach dem Laden.
    private(set) var vocabulary = CustomVocabulary.disabled
    private var vocabularyTask: Task<Void, Never>?

    /// Einheitlicher Statustext für Menü, Overlay und Einstellungen.
    var statusText: String {
        switch state {
        case .idle: return "Kein Modell geladen"
        case .loading(let p):
            if p < 0.9 { return "Modell wird geladen… \(Int(p / 0.9 * 100)) %" }
            if p < 0.95 { return "Modell wird initialisiert…" }
            return "Modell wird aufgewärmt (beim ersten Mal ca. 1 Minute)…"
        case .ready(let kind): return "Bereit – \(kind.shortLabel)"
        case .failed(let msg): return "Fehler: \(msg)"
        }
    }

    /// Hinweis im Overlay, wenn ein Diktat mangels Modell nicht starten kann.
    var noticeText: String {
        if case .failed(let message) = state { return "Modell konnte nicht geladen werden: \(message)" }
        return statusText
    }

    private var engine: TranscriptionEngine?
    private var loadTask: Task<Void, Never>?

    var current: TranscriptionEngine? { engine?.isLoaded == true ? engine : nil }

    var isReady: Bool {
        if case .ready = state { return true }
        return false
    }

    /// Lädt die gewünschte Engine (falls nicht bereits geladen). Entlädt vorher die alte.
    func ensureLoaded(_ kind: EngineKind) {
        if let engine, engine.kind == kind, engine.isLoaded { return }
        loadTask?.cancel()

        engine?.unload()
        let newEngine: TranscriptionEngine = (kind == .cohere) ? CohereEngine() : ParakeetEngine()
        engine = newEngine
        state = .loading(0)
        vocabularyState = vocabulary.isEmpty ? .off : .loading

        loadTask = Task { [weak self] in
            do {
                try await newEngine.prepare { fraction in
                    Task { @MainActor in
                        self?.updateProgress(fraction, for: newEngine)
                    }
                }
                guard !Task.isCancelled, self?.engine === newEngine else { return }
                self?.state = .ready(kind)
                self?.applyVocabulary(to: newEngine)
            } catch {
                guard !Task.isCancelled, self?.engine === newEngine else { return }
                self?.state = .failed(error.localizedDescription)
            }
        }
    }

    private func updateProgress(_ fraction: Double, for target: TranscriptionEngine) {
        guard engine === target, case .loading = state else { return }
        state = .loading(fraction)
    }

    /// Übernimmt ein neues Wörterbuch. Ist gerade keine Engine bereit, bekommt sie es nach dem Laden.
    func updateVocabulary(_ vocabulary: CustomVocabulary) {
        guard vocabulary != self.vocabulary else { return }
        self.vocabulary = vocabulary
        if let engine = current { applyVocabulary(to: engine) }
    }

    /// Aufrufe laufen nacheinander: Jeder Task wartet auf seinen Vorgänger, damit nie eine ältere
    /// Boosting-Session eine neuere überschreibt.
    private func applyVocabulary(to target: TranscriptionEngine) {
        let vocabulary = self.vocabulary
        let previous = vocabularyTask
        vocabularyState = vocabulary.isEmpty ? .off : .loading
        vocabularyTask = Task { [weak self] in
            await previous?.value
            let result: VocabularyState
            do {
                let boosted = try await target.configureVocabulary(vocabulary)
                result = vocabulary.isEmpty ? .off : .ready(terms: vocabulary.entries.count, boosted: boosted)
            } catch {
                result = .failed(error.localizedDescription)
            }
            guard let self, self.engine === target, self.vocabulary == vocabulary else { return }
            self.vocabularyState = result
        }
    }

    func retry(_ kind: EngineKind) {
        engine = nil
        ensureLoaded(kind)
    }
}
