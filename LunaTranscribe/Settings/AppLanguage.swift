import Foundation
import FluidAudio

/// Alle Sprachen, die mindestens eine Engine unterstützt.
/// Cohere: 14 Sprachen. Parakeet v3: 25 europäische Sprachen.
enum AppLanguage: String, CaseIterable, Identifiable, Codable {
    case german = "de"
    case english = "en"
    case french = "fr"
    case italian = "it"
    case spanish = "es"
    case portuguese = "pt"
    case dutch = "nl"
    case polish = "pl"
    case greek = "el"
    case arabic = "ar"
    case chinese = "zh"
    case japanese = "ja"
    case korean = "ko"
    case vietnamese = "vi"
    // Nur Parakeet v3
    case romanian = "ro"
    case danish = "da"
    case swedish = "sv"
    case finnish = "fi"
    case hungarian = "hu"
    case estonian = "et"
    case latvian = "lv"
    case lithuanian = "lt"
    case maltese = "mt"
    case czech = "cs"
    case slovak = "sk"
    case slovenian = "sl"
    case croatian = "hr"
    case bulgarian = "bg"
    case russian = "ru"
    case ukrainian = "uk"

    var id: String { rawValue }

    var displayName: String {
        Locale.current.localizedString(forLanguageCode: rawValue)?.capitalized ?? rawValue
    }

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
