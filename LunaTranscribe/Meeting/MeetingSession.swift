import Foundation
import Combine
import os

private let log = Logger(subsystem: "com.maximhofmeister.LunaTranscribe", category: "meeting")

/// Zustand des Meeting-Modus. Läuft unabhängig vom Diktat (`AppState`): eigener Recorder, eigene
/// Statusmaschine, kein Overlay. Während des Meetings wird nur aufgenommen – die gesamte
/// Verarbeitung (Sprecher, Transkription) passiert nach „Meeting beenden“.
@MainActor
final class MeetingSession: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording(since: Date)
        case processing(step: String, fraction: Double)
        case failed(String)
    }

    @Published private(set) var status: Status = .idle
    /// Laufzeit der aktuellen Aufnahme, einmal pro Sekunde aktualisiert.
    @Published private(set) var elapsed: TimeInterval = 0
    /// Systemaudio wurde für die laufende Aufnahme tatsächlich eingebunden.
    @Published private(set) var systemAudioActive = false
    /// Letztes fertiges Transkript – wird vom Ergebnisfenster angezeigt und dort umbenannt.
    @Published var transcript: MeetingTranscript?
    /// Pfad des automatischen Backups zum aktuellen Transkript.
    @Published private(set) var backupURL: URL?

    private let settings: SettingsStore
    private let models: ModelManager
    private let recorder = AudioRecorder()
    private let systemAudio = SystemAudioTap()
    /// Bleibt zwischen Meetings geladen (Modelle sind klein), spart die Kompilierung beim nächsten Mal.
    private let diarizer = SpeakerDiarizer()

    private var audioURL: URL?
    private var recordingStart: Date?
    private var elapsedTimer: Timer?
    private var processingTask: Task<Void, Never>?

    var isRecording: Bool {
        if case .recording = status { return true }
        return false
    }

    var isProcessing: Bool {
        if case .processing = status { return true }
        return false
    }

    init(settings: SettingsStore, models: ModelManager) {
        self.settings = settings
        self.models = models
        Self.removeOrphanedRecordings()
    }

    // MARK: Start / Stopp

    func start() {
        guard status == .idle || isFailed else { return }
        guard models.isReady else {
            status = .failed(models.statusText)
            return
        }

        Task {
            guard await Permissions.requestMicrophone() else {
                status = .failed("Kein Mikrofon-Zugriff. Bitte in den Systemeinstellungen erlauben.")
                Permissions.openMicrophoneSettings()
                return
            }

            let source = captureSource()
            let url = Self.temporaryAudioURL()
            do {
                try recorder.start(source: source, sink: .file(url))
            } catch {
                systemAudio.destroy()
                status = .failed(error.localizedDescription)
                return
            }

            audioURL = url
            recordingStart = Date()
            elapsed = 0
            status = .recording(since: recordingStart!)
            startElapsedTimer()
            log.notice("meeting recording started, systemAudio=\(self.systemAudioActive)")
        }
    }

    /// Beendet die Aufnahme und startet die Verarbeitung. Jeder Ausgang ist sichtbar (Status + Log),
    /// damit „Beenden“ nie stumm verpufft.
    func stop() {
        guard isRecording, let url = audioURL, let start = recordingStart else { return }
        stopCapture()

        let duration = recorder.recordedSeconds
        log.notice("meeting stopped after \(duration, format: .fixed(precision: 1)) s (elapsed \(self.elapsed, format: .fixed(precision: 0)) s)")
        guard duration >= 1 else {
            fail("Es kam kein Audio an (\(String(format: "%.1f", duration)) s aufgenommen). Mikrofon-/Systemaudio-Berechtigung prüfen.")
            return
        }
        guard let engine = models.current else {
            fail("Das Modell ist nicht geladen – Verarbeitung nicht möglich.")
            return
        }

        status = .processing(step: "Verarbeitung startet…", fraction: 0)
        let language = settings.languageForEngine(settings.primaryLanguage)
        let expectedSpeakers = settings.meetingSpeakerCount > 0 ? settings.meetingSpeakerCount : nil
        let transcriber = MeetingTranscriber(diarizer: diarizer, engine: engine)

        processingTask = Task.detached(priority: .userInitiated) { [weak self] in
            do {
                let result = try await transcriber.run(audioURL: url, recordedAt: start, language: language,
                                                       expectedSpeakers: expectedSpeakers) { progress in
                    Task { @MainActor in self?.status = .processing(step: progress.step, fraction: progress.fraction) }
                }
                await self?.finish(with: result)
            } catch is CancellationError {
                // Abbruch: Zustand wurde bereits in cancel() zurückgesetzt.
            } catch {
                await self?.fail(error.localizedDescription)
            }
        }
    }

    /// Bricht Aufnahme oder Verarbeitung ab und verwirft die Aufnahme.
    func cancel() {
        switch status {
        case .recording:
            stopCapture()
        case .processing:
            processingTask?.cancel()
            processingTask = nil
        case .idle, .failed:
            break
        }
        discardAudio()
        status = .idle
    }

    func dismissError() {
        if isFailed { status = .idle }
    }

    // MARK: Intern

    private var isFailed: Bool {
        if case .failed = status { return true }
        return false
    }

    private func captureSource() -> CaptureSource {
        systemAudioActive = false
        guard settings.meetingCaptureSystemAudio else {
            return .microphone(uid: settings.inputDeviceUID)
        }
        do {
            let source = try systemAudio.makeCaptureSource(microphoneUID: settings.inputDeviceUID)
            systemAudioActive = true
            return source
        } catch {
            // Kein harter Fehler: Meeting läuft mit Mikrofon weiter, das Menü zeigt den Hinweis.
            log.error("system audio unavailable, falling back to microphone: \(error.localizedDescription, privacy: .public)")
            return .microphone(uid: settings.inputDeviceUID)
        }
    }

    private func stopCapture() {
        stopElapsedTimer()
        _ = recorder.stop()
        systemAudio.destroy()
    }

    private func finish(with result: MeetingTranscript) {
        processingTask = nil
        let folder = MeetingExporter.folder(from: settings.meetingExportFolder)
        backupURL = try? MeetingExporter.writeBackup(result, to: folder)

        if settings.meetingKeepAudio, let url = audioURL {
            try? MeetingExporter.keepAudio(url, for: result, in: folder)
        }
        discardAudio()
        transcript = result
        status = .idle
        log.notice("meeting processed: \(result.turns.count) turns, backup=\(self.backupURL?.lastPathComponent ?? "-", privacy: .public)")
    }

    private func fail(_ message: String) {
        processingTask = nil
        discardAudio()
        status = .failed(message)
        log.error("meeting failed: \(message, privacy: .public)")
    }

    private func discardAudio() {
        if let url = audioURL { try? FileManager.default.removeItem(at: url) }
        audioURL = nil
        recordingStart = nil
    }

    private func startElapsedTimer() {
        elapsedTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, let start = self.recordingStart else { return }
                self.elapsed = Date().timeIntervalSince(start)
            }
        }
    }

    private func stopElapsedTimer() {
        elapsedTimer?.invalidate()
        elapsedTimer = nil
    }

    private static var temporaryFolder: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
            .appendingPathComponent("LunaTranscribe/Meetings", isDirectory: true)
    }

    /// Temporäre Aufnahme unter ~/Library/Caches – wird nach der Verarbeitung gelöscht.
    private static func temporaryAudioURL() -> URL {
        temporaryFolder.appendingPathComponent("\(UUID().uuidString).wav")
    }

    /// Reste von abgestürzten oder beim Beenden der App noch laufenden Meetings entfernen.
    private static func removeOrphanedRecordings() {
        guard let files = try? FileManager.default.contentsOfDirectory(at: temporaryFolder, includingPropertiesForKeys: nil) else { return }
        for file in files { try? FileManager.default.removeItem(at: file) }
    }
}
