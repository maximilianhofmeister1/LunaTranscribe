import Foundation
import Combine
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
@MainActor
final class SettingsStore: ObservableObject {
    static let shared = SettingsStore()

    private let d = UserDefaults.standard

    @Published var engine: EngineKind { didSet { d.set(engine.rawValue, forKey: "engine") } }
    @Published var primaryLanguage: AppLanguage { didSet { d.set(primaryLanguage.rawValue, forKey: "primaryLanguage") } }
    @Published var secondaryLanguage: AppLanguage { didSet { d.set(secondaryLanguage.rawValue, forKey: "secondaryLanguage") } }
    /// "" = System-Standardmikrofon
    @Published var inputDeviceUID: String { didSet { d.set(inputDeviceUID, forKey: "inputDeviceUID") } }
    @Published var recordingMode: RecordingMode { didSet { d.set(recordingMode.rawValue, forKey: "recordingMode") } }
    @Published var playSounds: Bool { didSet { d.set(playSounds, forKey: "playSounds") } }
    @Published var keepHistory: Bool { didSet { d.set(keepHistory, forKey: "keepHistory") } }
    @Published var pauseMedia: Bool { didSet { d.set(pauseMedia, forKey: "pauseMedia") } }

    // Meeting-Modus
    /// Zusätzlich zum Mikrofon die Systemwiedergabe (Zoom, Teams …) aufnehmen.
    @Published var meetingCaptureSystemAudio: Bool { didSet { d.set(meetingCaptureSystemAudio, forKey: "meetingCaptureSystemAudio") } }
    /// Aufnahme (WAV) nach der Verarbeitung neben dem Transkript behalten statt löschen.
    @Published var meetingKeepAudio: Bool { didSet { d.set(meetingKeepAudio, forKey: "meetingKeepAudio") } }
    /// Ablageordner für Backups/Audio; "" = ~/Documents/LunaTranscribe Meetings
    @Published var meetingExportFolder: String { didSet { d.set(meetingExportFolder, forKey: "meetingExportFolder") } }
    /// Erwartete Teilnehmerzahl für die Sprechererkennung; 0 = automatisch.
    @Published var meetingSpeakerCount: Int { didSet { d.set(meetingSpeakerCount, forKey: "meetingSpeakerCount") } }

    // Nachbearbeitung (dritter Hotkey)
    /// Sprachmodell, das Diktate bereinigt.
    @Published var refinerKind: RefinerKind { didSet { d.set(refinerKind.rawValue, forKey: "refinerKind") } }
    /// System-Anweisung für das Sprachmodell, in den Einstellungen editierbar (`{language}` = Diktatsprache).
    /// Gespeichert wird nur eine Abweichung vom Standard; `resetRefinePrompt()` stellt ihn wieder her.
    @Published var refinePrompt: String {
        didSet {
            if refinePrompt == RefinePrompt.defaultTemplate { d.removeObject(forKey: "refinePrompt") }
            else { d.set(refinePrompt, forKey: "refinePrompt") }
        }
    }
    var isRefinePromptDefault: Bool { refinePrompt == RefinePrompt.defaultTemplate }
    func resetRefinePrompt() { refinePrompt = RefinePrompt.defaultTemplate }

    /// Autostart bei der Anmeldung über SMAppService (macOS 13+). Der Zustand lebt im System,
    /// nicht in UserDefaults – deshalb wird er hier gelesen und beim Setzen direkt registriert.
    @Published var launchAtLogin: Bool {
        didSet {
            guard launchAtLogin != oldValue else { return }
            do {
                if launchAtLogin { try SMAppService.mainApp.register() }
                else { try SMAppService.mainApp.unregister() }
            } catch {
                launchAtLoginError = error.localizedDescription
                launchAtLogin = oldValue
            }
        }
    }
    @Published var launchAtLoginError: String?

    private init() {
        engine = EngineKind(rawValue: d.string(forKey: "engine") ?? "") ?? .parakeet
        primaryLanguage = AppLanguage(rawValue: d.string(forKey: "primaryLanguage") ?? "") ?? .german
        secondaryLanguage = AppLanguage(rawValue: d.string(forKey: "secondaryLanguage") ?? "") ?? .english
        inputDeviceUID = d.string(forKey: "inputDeviceUID") ?? ""
        recordingMode = RecordingMode(rawValue: d.string(forKey: "recordingMode") ?? "") ?? .hold
        playSounds = d.object(forKey: "playSounds") as? Bool ?? true
        keepHistory = d.object(forKey: "keepHistory") as? Bool ?? true
        pauseMedia = d.object(forKey: "pauseMedia") as? Bool ?? true
        meetingCaptureSystemAudio = d.object(forKey: "meetingCaptureSystemAudio") as? Bool ?? true
        meetingKeepAudio = d.object(forKey: "meetingKeepAudio") as? Bool ?? false
        meetingExportFolder = d.string(forKey: "meetingExportFolder") ?? ""
        meetingSpeakerCount = d.integer(forKey: "meetingSpeakerCount")
        refinerKind = RefinerKind(rawValue: d.string(forKey: "refinerKind") ?? "") ?? .lfm2_1_2b
        refinePrompt = d.string(forKey: "refinePrompt") ?? RefinePrompt.defaultTemplate
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    /// Stellt sicher, dass die gewählte Sprache zur Engine passt.
    func languageForEngine(_ lang: AppLanguage) -> AppLanguage {
        lang.isSupported(by: engine) ? lang : (engine == .cohere ? .english : .english)
    }
}
