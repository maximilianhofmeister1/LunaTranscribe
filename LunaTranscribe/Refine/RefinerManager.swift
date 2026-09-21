import Foundation
import Combine

/// Hält genau ein geladenes Nachbearbeitungs-Modell und wechselt es bei Bedarf.
/// Gegenstück zu `ModelManager` für die ASR – bewusst dieselbe Form, damit Menü, Overlay und
/// Einstellungen beide Modell-Familien gleich behandeln können.
@MainActor
final class RefinerManager: ObservableObject {
    enum State: Equatable {
        case idle
        case loading(Double)
        case ready(RefinerKind)
        case failed(String)
    }

    @Published private(set) var state: State = .idle

    var statusText: String {
        switch state {
        case .idle: return "Kein Sprachmodell geladen"
        case .loading(let p):
            if p < 0.9 { return "Sprachmodell wird geladen… \(Int(p / 0.9 * 100)) %" }
            return "Sprachmodell wird aufgewärmt…"
        case .ready(let kind): return "Bereit – \(kind.shortLabel)"
        case .failed(let msg): return "Fehler: \(msg)"
        }
    }

    private var refiner: TextRefiner?
    private var loadTask: Task<Void, Never>?

    var current: TextRefiner? { refiner?.isLoaded == true ? refiner : nil }

    var isReady: Bool {
        if case .ready = state { return true }
        return false
    }

    /// Lädt das gewünschte Modell (falls nicht bereits geladen). Entlädt vorher das alte.
    func ensureLoaded(_ kind: RefinerKind) {
        if let refiner, refiner.kind == kind, refiner.isLoaded { return }
        loadTask?.cancel()
        refiner?.unload()

        guard let newRefiner = Self.make(kind) else {
            refiner = nil
            if case .unavailable(let reason) = kind.availability { state = .failed(reason) }
            return
        }
        refiner = newRefiner
        state = .loading(0)

        loadTask = Task { [weak self] in
            do {
                try await newRefiner.prepare { fraction in
                    Task { @MainActor in self?.updateProgress(fraction, for: newRefiner) }
                }
                guard !Task.isCancelled, self?.refiner === newRefiner else { return }
                self?.state = .ready(kind)
            } catch {
                guard !Task.isCancelled, self?.refiner === newRefiner else { return }
                self?.state = .failed(error.localizedDescription)
            }
        }
    }

    func retry(_ kind: RefinerKind) {
        refiner = nil
        ensureLoaded(kind)
    }

    private static func make(_ kind: RefinerKind) -> TextRefiner? {
        switch kind {
        case .lfm2_1_2b, .qwen3_1_7b, .qwen3_4b:
            return MLXRefiner(kind: kind)
        case .appleIntelligence:
            guard #available(macOS 26, *) else { return nil }
            return AppleIntelligenceRefiner()
        }
    }

    private func updateProgress(_ fraction: Double, for target: TextRefiner) {
        guard refiner === target, case .loading = state else { return }
        state = .loading(fraction)
    }
}
