import Foundation
import FoundationModels

/// Sprachmodelle für die Diktat-Nachbearbeitung. Gleiche Rolle wie `EngineKind` für die ASR.
enum RefinerKind: String, CaseIterable, Identifiable {
    case lfm2_1_2b          // Standard: schnellstes brauchbares Modell (~0,8 s pro Diktat)
    case qwen3_1_7b         // Bessere Qualität, etwas langsamer (~1 s)
    case qwen3_4b           // Beste Qualität (Selbstkorrekturen), ~2–3 s
    case appleIntelligence  // Apples System-Modell (macOS 26+), kein Download, Neural Engine

    var id: String { rawValue }

    var label: String {
        switch self {
        case .lfm2_1_2b: return "LFM2 1.2B (empfohlen – ~0,8 s pro Diktat, ~0,7 GB Download, ~0,9 GB RAM)"
        case .qwen3_1_7b: return "Qwen3 1.7B (genauer – ~1 s pro Diktat, ~1 GB Download, ~1,3 GB RAM)"
        case .qwen3_4b: return "Qwen3 4B (beste Qualität – ~2–3 s pro Diktat, ~2,3 GB Download, ~2,8 GB RAM)"
        case .appleIntelligence: return "Apple Intelligence (Systemmodell, kein Download, macOS 26+)"
        }
    }

    var shortLabel: String {
        switch self {
        case .lfm2_1_2b: return "LFM2 1.2B"
        case .qwen3_1_7b: return "Qwen3 1.7B"
        case .qwen3_4b: return "Qwen3 4B"
        case .appleIntelligence: return "Apple Intelligence"
        }
    }

    /// Hugging-Face-Repo für MLX-Modelle; nil für das Systemmodell.
    var huggingFaceID: String? {
        switch self {
        case .lfm2_1_2b: return "mlx-community/LFM2-1.2B-4bit"
        case .qwen3_1_7b: return "mlx-community/Qwen3-1.7B-4bit"
        case .qwen3_4b: return "mlx-community/Qwen3-4B-4bit"
        case .appleIntelligence: return nil
        }
    }

    enum Availability: Equatable {
        case available
        case unavailable(reason: String)
    }

    /// Kann das Modell auf diesem Mac grundsätzlich genutzt werden? MLX-Modelle immer (Download
    /// passiert beim Laden); Apple Intelligence nur, wenn das System es freigibt.
    var availability: Availability {
        guard self == .appleIntelligence else { return .available }
        guard #available(macOS 26, *) else {
            return .unavailable(reason: "Benötigt macOS 26 oder neuer.")
        }
        switch SystemLanguageModel.default.availability {
        case .available:
            return .available
        case .unavailable(let reason):
            return .unavailable(reason: Self.describe(reason))
        }
    }

    var isAvailable: Bool { availability == .available }

    @available(macOS 26, *)
    private static func describe(_ reason: SystemLanguageModel.Availability.UnavailableReason) -> String {
        switch reason {
        case .deviceNotEligible:
            return "Dieser Mac unterstützt Apple Intelligence nicht."
        case .appleIntelligenceNotEnabled:
            return "Apple Intelligence ist nicht aktiviert (Systemeinstellungen → Apple Intelligence & Siri)."
        case .modelNotReady:
            return "Das Systemmodell ist noch nicht bereit – Apple Intelligence einschalten und warten, bis der Download abgeschlossen ist. Siri- und Gerätesprache müssen übereinstimmen."
        @unknown default:
            return "Apple Intelligence ist derzeit nicht verfügbar."
        }
    }
}

/// Gemeinsame Schnittstelle der Nachbearbeitungs-Backends (MLX-Modelle, Apple Intelligence).
/// Zustandslos pro Aufruf: jedes Diktat wird unabhängig vom vorherigen bereinigt.
protocol TextRefiner: AnyObject {
    var kind: RefinerKind { get }
    var isLoaded: Bool { get }

    /// Lädt das Modell (MLX: beim ersten Mal Download von Hugging Face) und wärmt es auf.
    /// `progress` liegt zwischen 0 und 1.
    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws

    /// Gibt den bereinigten Text zurück. Wirft bei Fehlern – der Aufrufer fällt dann auf den Rohtext zurück.
    /// - Parameter promptTemplate: System-Anweisung mit `{language}`-Platzhalter (siehe `RefinePrompt`).
    func refine(_ text: String, language: AppLanguage, promptTemplate: String) async throws -> String

    func unload()
}

enum RefinerError: LocalizedError {
    case notLoaded
    case unavailable(String)
    case timeout
    case implausibleOutput

    var errorDescription: String? {
        switch self {
        case .notLoaded: return "Das Sprachmodell ist noch nicht geladen."
        case .unavailable(let reason): return reason
        case .timeout: return "Die Nachbearbeitung hat zu lange gedauert."
        case .implausibleOutput: return "Das Sprachmodell hat keinen brauchbaren Text geliefert."
        }
    }
}
