import Foundation

/// In welcher Sprache Zusammenfassungen entstehen: wie das Meeting oder immer in einer festen Sprache.
/// Der Standard steht in den Einstellungen (`SharedSettings.Key.summaryLanguage`); im Meeting lässt er
/// sich für die nächste Zusammenfassung überschreiben.
///
/// Gespeichert wird der Rohwert: „meeting“ oder der Sprachcode („de“, „en“ …). Rohwerte sind
/// gespeicherte Daten – nie ändern.
public enum SummaryLanguage: Hashable, Sendable {
    case meeting
    case fixed(AppLanguage)

    /// Die tatsächliche Sprache für ein Meeting in `meetingLanguage`.
    public func resolved(for meetingLanguage: AppLanguage) -> AppLanguage {
        switch self {
        case .meeting: return meetingLanguage
        case .fixed(let language): return language
        }
    }
}

extension SummaryLanguage: RawRepresentable {
    private static let meetingRawValue = "meeting"

    public init?(rawValue: String) {
        if rawValue == Self.meetingRawValue {
            self = .meeting
        } else if let language = AppLanguage(rawValue: rawValue) {
            self = .fixed(language)
        } else {
            return nil
        }
    }

    public var rawValue: String {
        switch self {
        case .meeting: return Self.meetingRawValue
        case .fixed(let language): return language.rawValue
        }
    }
}

extension SummaryLanguage: Identifiable {
    public var id: String { rawValue }
}

extension AppLanguage {
    /// Alle Sprachen nach deutschem Anzeigenamen sortiert – für Auswahllisten.
    public static var sortedByDisplayName: [AppLanguage] {
        allCases.sorted { $0.displayName.localizedStandardCompare($1.displayName) == .orderedAscending }
    }
}
