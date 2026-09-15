import AppKit
import Combine
import KeyboardShortcuts

struct Transcript: Identifiable {
    let id = UUID()
    let date: Date
    let text: String
    let language: AppLanguage
    let durationSeconds: Double
    let processingSeconds: Double
}

/// Zentraler Zustand der App. Verbindet Hotkeys, Aufnahme, Engine und Texteinfügen.
@MainActor
final class AppState: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording
        case transcribing
        case notice(String)
    }

    @Published private(set) var status: Status = .idle
    @Published private(set) var history: [Transcript] = []
    @Published private(set) var accessibilityGranted = Permissions.accessibilityGranted
    @Published private(set) var inputLevel: Float = 0

    let settings = SettingsStore.shared
    let models = ModelManager()
    private let recorder = AudioRecorder()
    private let overlay = OverlayController()

    private var activeLanguage: AppLanguage = .german
    private var recordingStart: Date?
    private var levelTimer: Timer?
    private var cancellables = Set<AnyCancellable>()
    /// Zählt Transkriptionen; ein Abbruch erhöht den Wert, sodass ein verspätetes Ergebnis verworfen wird.
    private var transcriptionGeneration = 0
    private var escapeMonitors: [Any] = []

    init() {
        registerHotkeys()

        // Modell direkt beim Start laden, damit der erste Hotkey sofort reagiert.
        models.ensureLoaded(settings.engine)
        settings.$engine
            .dropFirst()
            .removeDuplicates()
            .sink { [weak self] kind in self?.models.ensureLoaded(kind) }
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

    /// Escape bricht Aufnahme oder laufende Transkription ab – nur solange etwas läuft,
    /// sonst wird die Taste nicht angefasst.
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
            playSound("Tink")
            status = .idle
        case .transcribing:
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
    }

    private func language(for slot: HotkeySlot) -> AppLanguage {
        let raw = slot == .primary ? settings.primaryLanguage : settings.secondaryLanguage
        return settings.languageForEngine(raw)
    }

    private func hotkeyDown(_ slot: HotkeySlot) {
        switch status {
        case .recording:
            if settings.recordingMode == .toggle { stopRecordingAndTranscribe() }
        case .idle, .notice:
            startRecording(language: language(for: slot))
        case .transcribing:
            break
        }
    }

    private func hotkeyUp(_ slot: HotkeySlot) {
        guard settings.recordingMode == .hold, status == .recording else { return }
        stopRecordingAndTranscribe()
    }

    // MARK: Aufnahme

    func toggleRecordingFromMenu() {
        if status == .recording { stopRecordingAndTranscribe() } else { startRecording(language: language(for: .primary)) }
    }

    private func startRecording(language: AppLanguage) {
        guard models.isReady, let _ = models.current else {
            showNotice(modelNoticeText())
            return
        }

        Task {
            guard await Permissions.requestMicrophone() else {
                showNotice("Kein Mikrofon-Zugriff. Bitte in den Systemeinstellungen erlauben.")
                Permissions.openMicrophoneSettings()
                return
            }
            do {
                try recorder.start(deviceUID: settings.inputDeviceUID)
            } catch {
                showNotice(error.localizedDescription)
                return
            }
            activeLanguage = language
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
        playSound("Tink")

        // Zu kurz (versehentlicher Tastendruck) → ignorieren
        guard duration >= 0.3, let engine = models.current else {
            status = .idle
            return
        }

        status = .transcribing
        let language = activeLanguage
        let keepHistory = settings.keepHistory
        transcriptionGeneration += 1
        let generation = transcriptionGeneration

        Task {
            let t0 = Date()
            do {
                let text = try await engine.transcribe(samples, language: language)
                // `samples` verlässt hier den Scope – die Aufnahme existiert danach nirgends mehr.
                let elapsed = Date().timeIntervalSince(t0)

                // Wurde inzwischen abgebrochen (Escape/X)? Dann Ergebnis verwerfen.
                guard generation == transcriptionGeneration else { return }

                if text.isEmpty {
                    showNotice("Nichts erkannt.")
                } else {
                    TextInjector.insert(text)
                    if keepHistory {
                        history.insert(Transcript(date: Date(), text: text, language: language,
                                                  durationSeconds: duration, processingSeconds: elapsed), at: 0)
                        if history.count > 20 { history.removeLast(history.count - 20) }
                    }
                    status = .idle
                }
            } catch {
                guard generation == transcriptionGeneration else { return }
                showNotice("Fehler: \(error.localizedDescription)")
            }
        }
    }

    func clearHistory() { history.removeAll() }

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
