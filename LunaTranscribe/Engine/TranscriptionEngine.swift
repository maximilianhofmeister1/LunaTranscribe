import Foundation
import LunaCore

/// Gemeinsame Schnittstelle. Später kann hier z. B. eine MLX-Engine ergänzt werden,
/// ohne dass sich UI oder Aufnahme-Logik ändern.
protocol TranscriptionEngine: AnyObject {
    var kind: EngineKind { get }
    var isLoaded: Bool { get }

    /// Lädt das Modell (lädt es beim ersten Mal von Hugging Face herunter) und wärmt es auf.
    /// `progress` liegt zwischen 0 und 1 (0–0.9 Download, 0.9–0.95 Laden, 0.95–1 Warm-up).
    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws

    /// 16 kHz, mono, Float32. Gibt den erkannten Text zurück (getrimmt, Wörterbuch angewendet).
    func transcribe(_ samples: [Float], language: AppLanguage) async throws -> String

    /// Setzt das eigene Wörterbuch für alle folgenden Transkriptionen. Die Schreibweisen-Korrektur
    /// gilt sofort; Engines mit akustischem Boosting laden dafür ggf. ein Zusatzmodell und werfen,
    /// wenn das scheitert – die Korrektur bleibt dann trotzdem aktiv.
    /// - Returns: `true`, wenn akustisches Boosting aktiv ist.
    func configureVocabulary(_ vocabulary: CustomVocabulary) async throws -> Bool

    func unload()
}

enum EngineError: LocalizedError {
    case notLoaded
    case unsupportedLanguage(AppLanguage, EngineKind)

    var errorDescription: String? {
        switch self {
        case .notLoaded: return "Das Modell ist noch nicht geladen."
        case .unsupportedLanguage(let l, let e): return "\(l.displayName) wird von \(e.shortLabel) nicht unterstützt."
        }
    }
}
