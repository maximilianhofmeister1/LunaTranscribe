import Foundation

/// Alle Sprachen, die mindestens eine Engine unterstützt.
/// Cohere: 14 Sprachen. Parakeet v3: 25 europäische Sprachen.
/// Die Zuordnung zu den FluidAudio-Typen (und damit „welche Engine kann was“) ergänzt die App.
public enum AppLanguage: String, CaseIterable, Identifiable, Codable, Sendable {
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

    public var id: String { rawValue }

    /// Deutscher Name („Deutsch“, „Englisch“) – die Oberfläche ist deutsch, unabhängig von der Systemsprache.
    public var displayName: String {
        Locale(identifier: "de").localizedString(forLanguageCode: rawValue)?.capitalized ?? rawValue
    }
}
