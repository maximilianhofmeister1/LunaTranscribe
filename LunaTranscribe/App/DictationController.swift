import AppKit
import Combine

/// Ein fertiges Diktat im Verlauf (nur im RAM).
struct Transcript: Identifiable {
    let id = UUID()
    let date: Date
    var text: String
    let language: AppLanguage
    let durationSeconds: Double
    let processingSeconds: Double
    /// Unbereinigtes ASR-Ergebnis, nur gesetzt, wenn `text` vom Sprachmodell nachbearbeitet wurde.
    var rawText: String? = nil

    var wordCount: Int { text.split(whereSeparator: \.isWhitespace).count }
}

/// Was ein Hotkey-Druck auslöst: Sprache des Diktats und ob das Sprachmodell nachbearbeitet.
struct DictationRequest: Equatable {
    let language: AppLanguage
    let refine: Bool
}

/// Statusmaschine eines Diktats: Aufnahme → Transkription → optional Nachbearbeitung → Einfügen.
///
/// Invariante: **Ein Diktat geht nie verloren.** Scheitert die Nachbearbeitung, wird der Rohtext
/// eingefügt und ein Hinweis gezeigt. Ein Abbruch (Escape/X) erhöht `generation`, sodass ein
/// verspätetes Ergebnis verworfen statt eingefügt wird.
@MainActor
final class DictationController: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording
        case transcribing
        /// Sprachmodell bereinigt den Text (nur dritter Hotkey).
        case refining
        case notice(String)

        /// Läuft gerade Transkription oder Nachbearbeitung?
        var isBusy: Bool { self == .transcribing || self == .refining }
    }

    static let historyLimit = 20

    @Published private(set) var status: Status = .idle
    @Published private(set) var history: [Transcript] = []
    @Published private(set) var inputLevel: Float = 0

    private let settings: SettingsStore
    private let models: ModelManager
    private let refiners: RefinerManager
    private let recorder = AudioRecorder()
    private let media = MediaPlaybackController()

    private var activeRequest = DictationRequest(language: .german, refine: false)
    private var levelTimer: Timer?
    private var generation = 0

    init(settings: SettingsStore, models: ModelManager, refiners: RefinerManager) {
        self.settings = settings
        self.models = models
        self.refiners = refiners
    }

    // MARK: Hotkeys & Menü

    func request(for slot: HotkeySlot) -> DictationRequest {
        switch slot {
        case .primary: return DictationRequest(language: settings.languageForEngine(settings.primaryLanguage), refine: false)
        case .secondary: return DictationRequest(language: settings.languageForEngine(settings.secondaryLanguage), refine: false)
        case .refined: return DictationRequest(language: settings.languageForEngine(settings.primaryLanguage), refine: true)
        }
    }

    func hotkeyDown(_ slot: HotkeySlot) {
        switch status {
        case .recording:
            if settings.recordingMode == .toggle { stopAndTranscribe() }
        case .idle, .notice:
            start(request(for: slot))
        case .transcribing, .refining:
            break
        }
    }

    func hotkeyUp(_ slot: HotkeySlot) {
        guard settings.recordingMode == .hold, status == .recording else { return }
        stopAndTranscribe()
    }

    /// Menü-Buttons: Diktat in der Hauptsprache, wahlweise mit Nachbearbeitung (wie Hotkey 1 bzw. 3).
    func toggleFromMenu(refine: Bool = false) {
        if status == .recording { stopAndTranscribe() } else { start(request(for: refine ? .refined : .primary)) }
    }

    /// Bricht Aufnahme oder laufende Verarbeitung ab (Escape, X im Overlay, Menü).
    func cancel() {
        switch status {
        case .recording:
            stopLevelTimer()
            _ = recorder.stop()   // Samples verwerfen
            media.resumeIfPaused()
            playSound("Tink")
            status = .idle
        case .transcribing, .refining:
            generation += 1   // laufendes Ergebnis wird ignoriert
            status = .idle
        case .notice:
            status = .idle
        case .idle:
            break
        }
    }

    // MARK: Verlauf

    func clearHistory() { history.removeAll() }

    /// Setzt Einträge direkt in den Verlauf (nur für Entwickler-Selbsttests).
    func injectHistory(_ transcripts: [Transcript]) { history = transcripts + history }

    /// Ersetzt den Text eines Verlaufseintrags (Korrektur durch den Nutzer).
    func updateHistoryText(id: Transcript.ID, to text: String) {
        guard let index = history.firstIndex(where: { $0.id == id }) else { return }
        history[index].text = text
    }

    // MARK: Ablauf

    private func start(_ request: DictationRequest) {
        guard models.isReady, models.current != nil else {
            showNotice(models.noticeText)
            return
        }

        let t0 = Date()
        let ms: @Sendable () -> Int = { Int(Date().timeIntervalSince(t0) * 1000) }
        Log.timing.notice("hotkey down")

        Task {
            guard await Permissions.requestMicrophone() else {
                showNotice("Kein Mikrofon-Zugriff. Bitte in den Systemeinstellungen erlauben.")
                Permissions.openMicrophoneSettings()
                return
            }
            Log.timing.notice("permission ok +\(ms(), privacy: .public)ms")

            // Wiedergabe parallel pausieren – darf den Mikrofonstart nicht aufhalten.
            if settings.pauseMedia {
                let media = self.media
                Task.detached(priority: .userInitiated) {
                    media.pauseIfPlaying()
                    Log.timing.notice("media paused +\(ms(), privacy: .public)ms")
                }
            }
            do {
                recorder.onFirstBuffer = { Log.timing.notice("first audio buffer +\(ms(), privacy: .public)ms") }
                try recorder.start(deviceUID: settings.inputDeviceUID)
                Log.timing.notice("recorder started +\(ms(), privacy: .public)ms")
            } catch {
                media.resumeIfPaused()
                showNotice(error.localizedDescription)
                return
            }
            activeRequest = request
            status = .recording
            playSound("Pop")
            startLevelTimer()
        }
    }

    private func stopAndTranscribe() {
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
        generation += 1
        let myGeneration = generation

        Task {
            let t0 = Date()
            do {
                let rawText = try await engine.transcribe(samples, language: request.language)
                // `samples` verlässt hier den Scope – die Aufnahme existiert danach nirgends mehr.
                Log.timing.notice("transcribed +\(Int(Date().timeIntervalSince(t0) * 1000), privacy: .public)ms")

                // Wurde inzwischen abgebrochen (Escape/X)? Dann Ergebnis verwerfen.
                guard myGeneration == generation else { return }
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
                    Log.timing.notice("refined +\(Int(Date().timeIntervalSince(t0) * 1000), privacy: .public)ms")
                    guard myGeneration == generation else { return }
                }

                TextInjector.insert(text)
                countForToday(text)
                remember(Transcript(date: Date(), text: text, language: request.language, durationSeconds: duration,
                                    processingSeconds: Date().timeIntervalSince(t0),
                                    rawText: text != rawText ? rawText : nil))
                if let fallbackNotice { showNotice(fallbackNotice) } else { status = .idle }
            } catch {
                guard myGeneration == generation else { return }
                showNotice("Fehler: \(error.localizedDescription)")
            }
        }
    }

    /// Lässt das Sprachmodell den Text bereinigen. Bei jedem Problem (Modell lädt noch, Fehler,
    /// Timeout, unplausible Antwort) kommt der Rohtext zurück, dazu ein Hinweis.
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
            Log.refine.error("refine failed: \(error.localizedDescription, privacy: .public)")
            return (rawText, "Nachbearbeitung fehlgeschlagen – Rohtext eingefügt.")
        }
    }

    /// Tageszähler der Übersicht – unabhängig vom Verlauf, speichert nur Zahlen.
    private func countForToday(_ text: String) {
        let words = text.split(whereSeparator: \.isWhitespace).count
        settings.dailyDictationStats = settings.dailyDictationStats.adding(words: words)
    }

    private func remember(_ transcript: Transcript) {
        guard settings.keepHistory else { return }
        history.insert(transcript, at: 0)
        if history.count > Self.historyLimit { history.removeLast(history.count - Self.historyLimit) }
    }

    // MARK: Helfer

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
