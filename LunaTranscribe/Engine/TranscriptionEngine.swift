import Foundation

enum EngineKind: String, CaseIterable, Identifiable {
    case parakeet   // Standard: ~0,2 s Latenz
    case cohere     // Beste Qualität, aber festes 35-s-Encoder-Fenster → ~2,5 s Latenz

    var id: String { rawValue }

    var label: String {
        switch self {
        case .cohere: return "Cohere Transcribe (beste Qualität, ~2,5 s pro Diktat, ~2,5 GB RAM)"
        case .parakeet: return "Parakeet TDT v3 (empfohlen – ~0,2 s pro Diktat, ~1 GB RAM)"
        }
    }

    var shortLabel: String {
        switch self {
        case .cohere: return "Cohere Transcribe"
        case .parakeet: return "Parakeet v3"
        }
    }
}

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

/// Basisordner für alle Modelle: ~/Library/Application Support/LunaTranscribe/Models
enum ModelPaths {
    static var base: URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport
            .appendingPathComponent("LunaTranscribe", isDirectory: true)
            .appendingPathComponent("Models", isDirectory: true)
    }
}
