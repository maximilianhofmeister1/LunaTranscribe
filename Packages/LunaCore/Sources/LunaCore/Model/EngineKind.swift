import Foundation

/// Die Spracherkennungs-Engines. Das Protokoll `TranscriptionEngine` und die Implementierungen
/// (FluidAudio) liegen in der App; hier steht nur, was auch in gespeicherten Meetings vorkommt.
public enum EngineKind: String, CaseIterable, Identifiable, Codable, Sendable {
    case parakeet   // Standard: ~0,2 s Latenz
    case cohere     // Beste Qualität, aber festes 35-s-Encoder-Fenster → ~2,5 s Latenz

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .cohere: return "Cohere Transcribe (beste Qualität, ~2,5 s pro Diktat, ~2,5 GB RAM)"
        case .parakeet: return "Parakeet TDT v3 (empfohlen – ~0,2 s pro Diktat, ~1 GB RAM)"
        }
    }

    public var shortLabel: String {
        switch self {
        case .cohere: return "Cohere Transcribe"
        case .parakeet: return "Parakeet v3"
        }
    }
}
