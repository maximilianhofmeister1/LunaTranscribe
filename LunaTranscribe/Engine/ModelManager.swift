import Foundation
import Combine

/// Hält genau eine geladene Engine im Speicher und wechselt sie bei Bedarf.
@MainActor
final class ModelManager: ObservableObject {
    enum State: Equatable {
        case idle
        case loading(Double)
        case ready(EngineKind)
        case failed(String)
    }

    @Published private(set) var state: State = .idle

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

        loadTask = Task { [weak self] in
            do {
                try await newEngine.prepare { fraction in
                    Task { @MainActor in
                        self?.updateProgress(fraction, for: newEngine)
                    }
                }
                guard !Task.isCancelled, self?.engine === newEngine else { return }
                self?.state = .ready(kind)
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

    func retry(_ kind: EngineKind) {
        engine = nil
        ensureLoaded(kind)
    }
}
