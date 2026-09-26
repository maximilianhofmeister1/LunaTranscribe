import FluidAudio
import Foundation
import LunaCore

/// Zuordnung der Sprachen (`AppLanguage`, im gemeinsamen Kern) zu den FluidAudio-Typen der Engines.
/// Liegt in der App, weil nur sie FluidAudio linkt – luna-mcp braucht nur Namen und Codes.
extension AppLanguage {
    /// Sprach-Enum von FluidAudio für Cohere Transcribe (nil = nicht unterstützt).
    var cohere: CohereAsrConfig.Language? { CohereAsrConfig.Language(rawValue: rawValue) }

    /// Sprach-Enum von FluidAudio für Parakeet v3 (nil = nicht unterstützt).
    var parakeet: Language? { Language(rawValue: rawValue) }  // FluidAudio.Language (Parakeet)

    func isSupported(by engine: EngineKind) -> Bool {
        switch engine {
        case .cohere: return cohere != nil
        case .parakeet: return parakeet != nil
        }
    }

    static func supported(by engine: EngineKind) -> [AppLanguage] {
        allCases.filter { $0.isSupported(by: engine) }
            .sorted { $0.displayName.localizedCompare($1.displayName) == .orderedAscending }
    }
}
