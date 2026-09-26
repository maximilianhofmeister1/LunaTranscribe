import Foundation
import Combine
import LunaCore
import ServiceManagement

enum RecordingMode: String, CaseIterable, Identifiable {
    case hold    // Taste gedrückt halten = aufnehmen
    case toggle  // 1× drücken = start, nochmal = stop
    var id: String { rawValue }
    var label: String {
        switch self {
        case .hold: return "Gedrückt halten"
        case .toggle: return "Ein/Aus umschalten"
        }
    }
}

/// Persistente Einstellungen (UserDefaults). Enthält keine Audiodaten und keine Transkripte.
/// Jede Eigenschaft speichert sich in `didSet` selbst; Schlüssel stehen gesammelt in `Key`.
@MainActor
final class SettingsStore: ObservableObject {
    static let shared = SettingsStore()

    // MARK: Diktat
    @Published var engine: EngineKind { didSet { save(engine, .engine) } }
    @Published var primaryLanguage: AppLanguage { didSet { save(primaryLanguage, .primaryLanguage) } }
    @Published var secondaryLanguage: AppLanguage { didSet { save(secondaryLanguage, .secondaryLanguage) } }
    /// "" = System-Standardmikrofon
    @Published var inputDeviceUID: String { didSet { save(inputDeviceUID, .inputDeviceUID) } }
    @Published var recordingMode: RecordingMode { didSet { save(recordingMode, .recordingMode) } }
    @Published var playSounds: Bool { didSet { save(playSounds, .playSounds) } }
    @Published var keepHistory: Bool { didSet { save(keepHistory, .keepHistory) } }
    @Published var pauseMedia: Bool { didSet { save(pauseMedia, .pauseMedia) } }
    /// Wörter und Diktate von heute (nur Zahlen) für die Übersicht.
    @Published var dailyDictationStats: DailyDictationStats { didSet { saveJSON(dailyDictationStats, .dailyDictationStats) } }

    // MARK: Meeting
    /// Zusätzlich zum Mikrofon die Systemwiedergabe (Zoom, Teams …) aufnehmen.
    @Published var meetingCaptureSystemAudio: Bool { didSet { save(meetingCaptureSystemAudio, .meetingCaptureSystemAudio) } }
    /// Aufnahme (WAV) nach der Verarbeitung im Meeting-Ordner behalten statt löschen.
    @Published var meetingKeepAudio: Bool { didSet { save(meetingKeepAudio, .meetingKeepAudio) } }
    /// Meeting-Ordner (Bibliothek); "" = ~/Documents/LunaTranscribe Meetings. Siehe `AppPaths.meetingFolder`.
    @Published var meetingFolder: String { didSet { save(meetingFolder, .meetingFolder) } }
    /// Erwartete Teilnehmerzahl für die Sprechererkennung; 0 = automatisch.
    @Published var meetingSpeakerCount: Int { didSet { save(meetingSpeakerCount, .meetingSpeakerCount) } }

    var meetingFolderURL: URL { AppPaths.meetingFolder(from: meetingFolder) }

    // MARK: Zusammenfassung (Claude CLI)
    /// Pfad zur Claude CLI; "" = automatisch suchen.
    @Published var claudeCLIPath: String { didSet { save(claudeCLIPath, .claudeCLIPath) } }
    /// Modell-Alias oder -Name für `claude --model`; "" = Standard der CLI.
    @Published var claudeModel: String { didSet { save(claudeModel, .claudeModel) } }
    /// Anweisung für die Zusammenfassung; gespeichert wird nur eine Abweichung vom Standard.
    @Published var summaryPrompt: String {
        didSet { saveUnlessDefault(summaryPrompt, default: SummaryPrompt.defaultTemplate, .summaryPrompt) }
    }
    /// Der einmalige Hinweis „Text geht an Anthropic“ wurde bestätigt.
    @Published var summaryCloudNoticeAccepted: Bool { didSet { save(summaryCloudNoticeAccepted, .summaryCloudNoticeAccepted) } }

    var isSummaryPromptDefault: Bool { summaryPrompt == SummaryPrompt.defaultTemplate }
    func resetSummaryPrompt() { summaryPrompt = SummaryPrompt.defaultTemplate }

    // MARK: Nachbearbeitung (dritter Hotkey)
    @Published var refinerKind: RefinerKind { didSet { save(refinerKind, .refinerKind) } }
    /// System-Anweisung für das Sprachmodell (`{language}` = Diktatsprache); gespeichert wird nur eine Abweichung vom Standard.
    @Published var refinePrompt: String {
        didSet { saveUnlessDefault(refinePrompt, default: RefinePrompt.defaultTemplate, .refinePrompt) }
    }
    var isRefinePromptDefault: Bool { refinePrompt == RefinePrompt.defaultTemplate }
    func resetRefinePrompt() { refinePrompt = RefinePrompt.defaultTemplate }

    // MARK: Wörterbuch
    /// Hauptschalter: Wörterbuch für Diktat und Meeting anwenden.
    @Published var vocabularyEnabled: Bool { didSet { save(vocabularyEnabled, .vocabularyEnabled) } }
    /// Einträge; bleiben erhalten, wenn der Schalter aus ist.
    @Published var vocabularyEntries: [VocabularyEntry] { didSet { saveJSON(vocabularyEntries, .vocabularyEntries) } }
    /// Aus Korrekturen gelernte Vorschläge, die noch bestätigt oder ignoriert werden müssen.
    @Published var vocabularySuggestions: [VocabularySuggestion] { didSet { saveJSON(vocabularySuggestions, .vocabularySuggestions) } }
    /// Ignorierte Vorschläge (`VocabularySuggestion.key`), damit sie nicht wiederkommen.
    @Published var ignoredSuggestionKeys: Set<String> { didSet { saveJSON(ignoredSuggestionKeys, .ignoredSuggestionKeys) } }

    /// Was die Engines tatsächlich anwenden: leer, wenn der Schalter aus ist.
    var activeVocabulary: CustomVocabulary {
        vocabularyEnabled ? CustomVocabulary(entries: vocabularyEntries) : .disabled
    }

    // MARK: Claude (MCP) und Suche
    // luna-mcp liest diese Werte aus derselben Domäne (`SharedSettings` in LunaCore, gleiche Schlüssel).

    /// Claude darf Meetings über luna-mcp lesen. Standard aus: Registrieren allein gibt nichts frei.
    @Published var mcpAccessEnabled: Bool { didSet { save(mcpAccessEnabled, .mcpAccessEnabled) } }
    /// Claude darf Meetings umbenennen, Sprecher benennen und – nach Bestätigung im Dialog – löschen.
    @Published var mcpWriteEnabled: Bool { didSet { save(mcpWriteEnabled, .mcpWriteEnabled) } }
    /// Auch Umbenennungen erst nach einem Bestätigungsdialog ausführen.
    @Published var mcpConfirmRenames: Bool { didSet { save(mcpConfirmRenames, .mcpConfirmRenames) } }
    /// Der einmalige Hinweis „Was Claude liest, geht an Anthropic“ wurde bestätigt.
    @Published var mcpCloudNoticeAccepted: Bool { didSet { save(mcpCloudNoticeAccepted, .mcpCloudNoticeAccepted) } }
    /// Inhaltliche Suche mit lokalem Embedding-Modell (einmaliger Download).
    @Published var semanticSearchEnabled: Bool { didSet { save(semanticSearchEnabled, .semanticSearchEnabled) } }

    // MARK: System

    /// Autostart bei der Anmeldung über SMAppService. Der Zustand lebt im System, nicht in
    /// UserDefaults – deshalb wird er hier gelesen und beim Setzen direkt registriert.
    @Published var launchAtLogin: Bool {
        didSet {
            guard launchAtLogin != oldValue else { return }
            do {
                if launchAtLogin { try SMAppService.mainApp.register() }
                else { try SMAppService.mainApp.unregister() }
                launchAtLoginError = nil
            } catch {
                launchAtLoginError = error.localizedDescription
                launchAtLogin = oldValue
            }
        }
    }
    @Published var launchAtLoginError: String?

    private init() {
        engine = Self.load(.engine, default: .parakeet)
        primaryLanguage = Self.load(.primaryLanguage, default: .german)
        secondaryLanguage = Self.load(.secondaryLanguage, default: .english)
        inputDeviceUID = Self.load(.inputDeviceUID, default: "")
        recordingMode = Self.load(.recordingMode, default: .hold)
        playSounds = Self.load(.playSounds, default: true)
        keepHistory = Self.load(.keepHistory, default: true)
        pauseMedia = Self.load(.pauseMedia, default: true)
        dailyDictationStats = Self.loadJSON(.dailyDictationStats, default: .empty)
        meetingCaptureSystemAudio = Self.load(.meetingCaptureSystemAudio, default: true)
        meetingKeepAudio = Self.load(.meetingKeepAudio, default: false)
        meetingFolder = Self.load(.meetingFolder, default: "")
        meetingSpeakerCount = Self.load(.meetingSpeakerCount, default: 0)
        claudeCLIPath = Self.load(.claudeCLIPath, default: "")
        claudeModel = Self.load(.claudeModel, default: "")
        summaryPrompt = Self.load(.summaryPrompt, default: SummaryPrompt.defaultTemplate)
        summaryCloudNoticeAccepted = Self.load(.summaryCloudNoticeAccepted, default: false)
        refinerKind = Self.load(.refinerKind, default: .lfm2_1_2b)
        refinePrompt = Self.load(.refinePrompt, default: RefinePrompt.defaultTemplate)
        vocabularyEnabled = Self.load(.vocabularyEnabled, default: true)
        vocabularyEntries = Self.loadJSON(.vocabularyEntries, default: [])
        vocabularySuggestions = Self.loadJSON(.vocabularySuggestions, default: [])
        ignoredSuggestionKeys = Self.loadJSON(.ignoredSuggestionKeys, default: [])
        mcpAccessEnabled = Self.load(.mcpAccessEnabled, default: false)
        mcpWriteEnabled = Self.load(.mcpWriteEnabled, default: false)
        mcpConfirmRenames = Self.load(.mcpConfirmRenames, default: false)
        mcpCloudNoticeAccepted = Self.load(.mcpCloudNoticeAccepted, default: false)
        semanticSearchEnabled = Self.load(.semanticSearchEnabled, default: false)
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    /// Die gewählte Sprache, falls die aktive Engine sie kann – sonst Englisch (können beide Engines).
    func languageForEngine(_ language: AppLanguage) -> AppLanguage {
        language.isSupported(by: engine) ? language : .english
    }

    // MARK: Persistenz

    /// UserDefaults-Schlüssel. Die Rohwerte sind gespeicherte Daten – nie umbenennen.
    private enum Key: String {
        case engine, primaryLanguage, secondaryLanguage, inputDeviceUID, recordingMode
        case playSounds, keepHistory, pauseMedia, dailyDictationStats
        case meetingCaptureSystemAudio, meetingKeepAudio, meetingSpeakerCount
        /// Historischer Name aus der Zeit, als der Ordner nur Backups/Exporte enthielt.
        case meetingFolder = "meetingExportFolder"
        case claudeCLIPath, claudeModel, summaryPrompt, summaryCloudNoticeAccepted
        case refinerKind, refinePrompt
        case vocabularyEnabled, vocabularyEntries, vocabularySuggestions, ignoredSuggestionKeys
        /// Auch in `SharedSettings.Key` (luna-mcp) – dort gleich halten.
        case mcpAccessEnabled, mcpWriteEnabled, mcpConfirmRenames, mcpCloudNoticeAccepted, semanticSearchEnabled
    }

    private static var defaults: UserDefaults { .standard }

    private static func load<T>(_ key: Key, default value: T) -> T {
        defaults.object(forKey: key.rawValue) as? T ?? value
    }

    private static func load<T: RawRepresentable>(_ key: Key, default value: T) -> T where T.RawValue == String {
        defaults.string(forKey: key.rawValue).flatMap(T.init(rawValue:)) ?? value
    }

    private static func loadJSON<T: Decodable>(_ key: Key, default value: T) -> T {
        defaults.data(forKey: key.rawValue).flatMap { try? JSONDecoder().decode(T.self, from: $0) } ?? value
    }

    private func save(_ value: Any, _ key: Key) {
        Self.defaults.set(value, forKey: key.rawValue)
    }

    private func save<T: RawRepresentable>(_ value: T, _ key: Key) where T.RawValue == String {
        Self.defaults.set(value.rawValue, forKey: key.rawValue)
    }

    private func saveJSON<T: Encodable>(_ value: T, _ key: Key) {
        Self.defaults.set(try? JSONEncoder().encode(value), forKey: key.rawValue)
    }

    /// Speichert nur Abweichungen vom Standard – so erreicht ein verbesserter Standard auch Nutzer,
    /// die den Text nie angefasst haben.
    private func saveUnlessDefault(_ value: String, default defaultValue: String, _ key: Key) {
        if value == defaultValue { Self.defaults.removeObject(forKey: key.rawValue) }
        else { Self.defaults.set(value, forKey: key.rawValue) }
    }
}
