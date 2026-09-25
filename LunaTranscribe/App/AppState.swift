import AppKit
import Combine
import KeyboardShortcuts
import os

private let timingLog = Logger(subsystem: "com.maximhofmeister.LunaTranscribe", category: "timing")

struct Transcript: Identifiable {
    let id = UUID()
    let date: Date
    let text: String
    let language: AppLanguage
    let durationSeconds: Double
    let processingSeconds: Double
    /// Unbereinigtes ASR-Ergebnis, nur gesetzt, wenn `text` vom Sprachmodell nachbearbeitet wurde.
    var rawText: String? = nil
}

/// Was ein Hotkey-Druck auslöst: Sprache des Diktats und ob das Sprachmodell nachbearbeitet.
struct DictationRequest: Equatable {
    let language: AppLanguage
    let refine: Bool
}

/// Zentraler Zustand der App. Verbindet Hotkeys, Aufnahme, Engine und Texteinfügen.
@MainActor
final class AppState: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording
        case transcribing
        /// Sprachmodell bereinigt den Text (nur dritter Hotkey).
        case refining
        case notice(String)
    }

    @Published private(set) var status: Status = .idle
    @Published private(set) var history: [Transcript] = []
    @Published private(set) var accessibilityGranted = Permissions.accessibilityGranted
    @Published private(set) var inputLevel: Float = 0

    let settings = SettingsStore.shared
    let models = ModelManager()
    /// Sprachmodell für die Nachbearbeitung (dritter Hotkey); wird wie die ASR beim Start geladen.
    let refiners = RefinerManager()
    /// Meeting-Modus: eigener Recorder und eigene Statusmaschine, läuft parallel zum Diktat.
    let meeting: MeetingSession
    private let recorder = AudioRecorder()
    private let overlay = OverlayController()
    private let media = MediaPlaybackController()
    private let meetingWindow = MeetingResultWindowController()

    private var activeRequest = DictationRequest(language: .german, refine: false)
    private var recordingStart: Date?
    private var levelTimer: Timer?
    private var cancellables = Set<AnyCancellable>()
    /// Zählt Transkriptionen; ein Abbruch erhöht den Wert, sodass ein verspätetes Ergebnis verworfen wird.
    private var transcriptionGeneration = 0
    private var escapeMonitors: [Any] = []

    /// - Parameter preloadModels: ASR- und Sprachmodell sofort laden (Normalfall). Selbsttests
    ///   laden ihre Modelle selbst und schalten das ab.
    init(preloadModels: Bool = true) {
        meeting = MeetingSession(settings: settings, models: models)
        meetingWindow.bind(to: meeting)
        // Menü und Menüleisten-Icon beobachten nur AppState – Meeting-Änderungen durchreichen.
        meeting.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
        // Meeting-Fehler zusätzlich als Dialog: das Menü-Popover ist nach dem Klick oft schon zu.
        meeting.$status
            .removeDuplicates()
            .sink { status in
                if case .failed(let message) = status { Self.showMeetingAlert(message) }
            }
            .store(in: &cancellables)
        registerHotkeys()

        // Modelle direkt beim Start laden, damit der erste Hotkey sofort reagiert.
        if preloadModels {
            models.ensureLoaded(settings.engine)
            refiners.ensureLoaded(settings.refinerKind)
        }
        settings.$engine
            .dropFirst()
            .removeDuplicates()
            .sink { [weak self] kind in self?.models.ensureLoaded(kind) }
            .store(in: &cancellables)
        settings.$refinerKind
            .dropFirst()
            .removeDuplicates()
            .sink { [weak self] kind in self?.refiners.ensureLoaded(kind) }
            .store(in: &cancellables)
        // Wörterbuch an die Engine geben – verzögert, damit nicht jeder Tastendruck in den
        // Einstellungen eine neue Boosting-Session baut. Der erste Wert kommt sofort.
        models.updateVocabulary(settings.activeVocabulary)
        settings.$vocabularyEnabled.combineLatest(settings.$vocabularyEntries)
            .dropFirst()
            .debounce(for: .milliseconds(600), scheduler: RunLoop.main)
            .sink { [weak self] enabled, entries in
                self?.models.updateVocabulary(enabled ? CustomVocabulary(entries: entries) : .disabled)
            }
            .store(in: &cancellables)
        // Menü und Einstellungen zeigen Lade- und Wörterbuch-Status des ModelManagers.
        models.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
        // Menü und Einstellungen beobachten nur AppState – Ladefortschritt des Sprachmodells durchreichen.
        refiners.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)

        overlay.bind(to: self)

        // Bedienungshilfen-Status regelmäßig prüfen (ändert sich in den Systemeinstellungen).
        Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.accessibilityGranted = Permissions.accessibilityGranted }
        }

        if !accessibilityGranted { Permissions.requestAccessibility() }
        installEscapeMonitor()
    }

    // MARK: Abbrechen (Escape / X im Overlay)

    /// Escape bricht Diktat-Aufnahme oder laufende Transkription ab – nur solange etwas läuft,
    /// sonst wird die Taste nicht angefasst. Ein laufendes Meeting ist davon bewusst ausgenommen:
    /// das wird nur über das Menü beendet, damit kein versehentlicher Tastendruck eine Stunde Aufnahme verwirft.
    private func installEscapeMonitor() {
        let handler: (NSEvent) -> Void = { [weak self] event in
            guard event.keyCode == 53 else { return }  // kVK_Escape
            Task { @MainActor in self?.cancel() }
        }
        if let global = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: handler) {
            escapeMonitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { event in
            handler(event)
            return event
        }) {
            escapeMonitors.append(local)
        }
    }

    func cancel() {
        switch status {
        case .recording:
            stopLevelTimer()
            _ = recorder.stop()   // Samples verwerfen
            media.resumeIfPaused()
            playSound("Tink")
            status = .idle
        case .transcribing, .refining:
            transcriptionGeneration += 1   // laufendes Ergebnis wird ignoriert
            status = .idle
        case .notice:
            status = .idle
        case .idle:
            break
        }
    }

    // MARK: Hotkeys

    private func registerHotkeys() {
        KeyboardShortcuts.onKeyDown(for: .recordPrimary) { [weak self] in self?.hotkeyDown(.primary) }
        KeyboardShortcuts.onKeyUp(for: .recordPrimary) { [weak self] in self?.hotkeyUp(.primary) }
        KeyboardShortcuts.onKeyDown(for: .recordSecondary) { [weak self] in self?.hotkeyDown(.secondary) }
        KeyboardShortcuts.onKeyUp(for: .recordSecondary) { [weak self] in self?.hotkeyUp(.secondary) }
        KeyboardShortcuts.onKeyDown(for: .recordRefined) { [weak self] in self?.hotkeyDown(.refined) }
        KeyboardShortcuts.onKeyUp(for: .recordRefined) { [weak self] in self?.hotkeyUp(.refined) }
    }

    private func request(for slot: HotkeySlot) -> DictationRequest {
        switch slot {
        case .primary: return DictationRequest(language: settings.languageForEngine(settings.primaryLanguage), refine: false)
        case .secondary: return DictationRequest(language: settings.languageForEngine(settings.secondaryLanguage), refine: false)
        case .refined: return DictationRequest(language: settings.languageForEngine(settings.primaryLanguage), refine: true)
        }
    }

    private func hotkeyDown(_ slot: HotkeySlot) {
        switch status {
        case .recording:
            if settings.recordingMode == .toggle { stopRecordingAndTranscribe() }
        case .idle, .notice:
            startRecording(request(for: slot))
        case .transcribing, .refining:
            break
        }
    }

    private func hotkeyUp(_ slot: HotkeySlot) {
        guard settings.recordingMode == .hold, status == .recording else { return }
        stopRecordingAndTranscribe()
    }

    // MARK: Aufnahme

    /// Menü-Buttons: Diktat in der Hauptsprache, wahlweise mit Nachbearbeitung (wie Hotkey 1 bzw. 3).
    func toggleRecordingFromMenu(refine: Bool = false) {
        if status == .recording { stopRecordingAndTranscribe() } else { startRecording(request(for: refine ? .refined : .primary)) }
    }

    private func startRecording(_ request: DictationRequest) {
        guard models.isReady, let _ = models.current else {
            showNotice(modelNoticeText())
            return
        }

        let t0 = Date()
        let ms: @Sendable () -> Int = { Int(Date().timeIntervalSince(t0) * 1000) }
        timingLog.notice("hotkey down")

        Task {
            guard await Permissions.requestMicrophone() else {
                showNotice("Kein Mikrofon-Zugriff. Bitte in den Systemeinstellungen erlauben.")
                Permissions.openMicrophoneSettings()
                return
            }
            timingLog.notice("permission ok +\(ms(), privacy: .public)ms")

            // Wiedergabe parallel pausieren – darf den Mikrofonstart nicht aufhalten.
            if settings.pauseMedia {
                let media = self.media
                Task.detached(priority: .userInitiated) {
                    media.pauseIfPlaying()
                    timingLog.notice("media paused +\(ms(), privacy: .public)ms")
                }
            }
            do {
                recorder.onFirstBuffer = { timingLog.notice("first audio buffer +\(ms(), privacy: .public)ms") }
                try recorder.start(deviceUID: settings.inputDeviceUID)
                timingLog.notice("recorder started +\(ms(), privacy: .public)ms")
            } catch {
                media.resumeIfPaused()
                showNotice(error.localizedDescription)
                return
            }
            activeRequest = request
            recordingStart = Date()
            status = .recording
            playSound("Pop")
            startLevelTimer()
        }
    }

    private func stopRecordingAndTranscribe() {
        stopLevelTimer()
        let samples = recorder.stop()
        let duration = Double(samples.count) / AudioRecorder.targetSampleRate
        media.resumeIfPaused()
        playSound("Tink")

        // Zu kurz (versehentlicher Tastendruck) → ignorieren
        guard duration >= 0.3, let engine = models.current else {
            status = .idle
            return
        }

        status = .transcribing
        let request = activeRequest
        let keepHistory = settings.keepHistory
        transcriptionGeneration += 1
        let generation = transcriptionGeneration

        Task {
            let t0 = Date()
            do {
                let rawText = try await engine.transcribe(samples, language: request.language)
                // `samples` verlässt hier den Scope – die Aufnahme existiert danach nirgends mehr.
                timingLog.notice("transcribed +\(Int(Date().timeIntervalSince(t0) * 1000), privacy: .public)ms")

                // Wurde inzwischen abgebrochen (Escape/X)? Dann Ergebnis verwerfen.
                guard generation == transcriptionGeneration else { return }
                guard !rawText.isEmpty else {
                    showNotice("Nichts erkannt.")
                    return
                }

                var text = rawText
                var fallbackNotice: String?
                if request.refine {
                    status = .refining
                    (text, fallbackNotice) = await refine(rawText, language: request.language)
                    // Das Sprachmodell darf Wörterbuch-Begriffe nicht „zurückkorrigieren“.
                    text = models.vocabulary.correct(text)
                    timingLog.notice("refined +\(Int(Date().timeIntervalSince(t0) * 1000), privacy: .public)ms")
                    guard generation == transcriptionGeneration else { return }
                }
                let elapsed = Date().timeIntervalSince(t0)

                TextInjector.insert(text)
                if keepHistory {
                    var entry = Transcript(date: Date(), text: text, language: request.language,
                                           durationSeconds: duration, processingSeconds: elapsed)
                    if text != rawText { entry.rawText = rawText }
                    history.insert(entry, at: 0)
                    if history.count > 20 { history.removeLast(history.count - 20) }
                }
                if let fallbackNotice { showNotice(fallbackNotice) } else { status = .idle }
            } catch {
                guard generation == transcriptionGeneration else { return }
                showNotice("Fehler: \(error.localizedDescription)")
            }
        }
    }

    /// Lässt das Sprachmodell den Text bereinigen. Ein Diktat geht nie verloren: Bei jedem Problem
    /// (Modell lädt noch, Fehler, Timeout, unplausible Antwort) kommt der Rohtext zurück, dazu ein Hinweis.
    private func refine(_ rawText: String, language: AppLanguage) async -> (text: String, notice: String?) {
        guard let refiner = refiners.current else {
            let reason: String
            if case .failed(let message) = refiners.state { reason = message } else { reason = "Sprachmodell lädt noch" }
            return (rawText, "\(reason) – Rohtext eingefügt.")
        }
        do {
            let refined = try await refiner.refine(rawText, language: language, promptTemplate: settings.refinePrompt)
            return (refined, nil)
        } catch is CancellationError {
            return (rawText, nil)
        } catch {
            timingLog.error("refine failed: \(error.localizedDescription, privacy: .public)")
            return (rawText, "Nachbearbeitung fehlgeschlagen – Rohtext eingefügt.")
        }
    }

    func clearHistory() { history.removeAll() }

    func showMeetingResult() { meetingWindow.show(session: meeting) }

    private static func showMeetingAlert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Meeting konnte nicht verarbeitet werden"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    // MARK: Helfer

    private func modelNoticeText() -> String {
        if case .failed(let msg) = models.state { return "Modell konnte nicht geladen werden: \(msg)" }
        return models.statusText
    }

    private func showNotice(_ text: String) {
        status = .notice(text)
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
            if case .notice = self?.status { self?.status = .idle }
        }
    }

    private func playSound(_ name: String) {
        guard settings.playSounds else { return }
        NSSound(named: NSSound.Name(name))?.play()
    }

    private func startLevelTimer() {
        levelTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 20, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.inputLevel = self.recorder.level
            }
        }
    }

    private func stopLevelTimer() {
        levelTimer?.invalidate()
        levelTimer = nil
        inputLevel = 0
    }
}
