import Foundation
import Combine
import LunaCore
import os

/// Aufnahme des Meeting-Modus. Läuft unabhängig vom Diktat (`AppState`): eigener Recorder, eigene
/// Statusmaschine, kein Overlay. Während des Meetings wird nur aufgenommen; „Beenden“ übergibt die
/// Aufnahme an die `MeetingProcessingQueue` und ist sofort wieder bereit für das nächste Meeting.
@MainActor
final class MeetingSession: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording(since: Date)
        case failed(String)
    }

    @Published private(set) var status: Status = .idle
    /// Laufzeit der aktuellen Aufnahme, einmal pro Sekunde aktualisiert.
    @Published private(set) var elapsed: TimeInterval = 0
    /// Systemaudio wurde für die laufende Aufnahme tatsächlich eingebunden.
    @Published private(set) var systemAudioActive = false
    private let settings: SettingsStore
    private let queue: MeetingProcessingQueue
    private let recorder = AudioRecorder()
    private let systemAudio = SystemAudioTap()

    private var audioURL: URL?
    private var recordingStart: Date?
    private var elapsedTimer: Timer?

    var isRecording: Bool {
        if case .recording = status { return true }
        return false
    }

    /// Die Aufnahme braucht kein geladenes Modell – die Warteschlange wartet selbst darauf.
    init(settings: SettingsStore, queue: MeetingProcessingQueue) {
        self.settings = settings
        self.queue = queue
        Self.removeOrphanedRecordings()
    }

    // MARK: Start / Stopp

    func start() {
        guard status == .idle || isFailed else { return }

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
            Log.meeting.notice("meeting recording started, systemAudio=\(self.systemAudioActive)")
        }
    }

    /// Beendet die Aufnahme und reiht sie zur Verarbeitung im Hintergrund ein. Jeder Ausgang ist
    /// sichtbar (Status + Log), damit „Beenden“ nie stumm verpufft.
    func stop() {
        guard isRecording, let url = audioURL, let start = recordingStart else { return }
        stopCapture()

        let duration = recorder.recordedSeconds
        Log.meeting.notice("meeting stopped after \(duration, format: .fixed(precision: 1)) s (elapsed \(self.elapsed, format: .fixed(precision: 0)) s)")
        guard duration >= 1 else {
            fail("Es kam kein Audio an (\(String(format: "%.1f", duration)) s aufgenommen). Mikrofon-/Systemaudio-Berechtigung prüfen.")
            return
        }

        do {
            try queue.enqueue(recording: url, recordedAt: start, durationSeconds: duration,
                              language: settings.languageForEngine(settings.primaryLanguage),
                              expectedSpeakers: settings.meetingSpeakerCount > 0 ? settings.meetingSpeakerCount : nil)
            audioURL = nil   // gehört jetzt der Warteschlange
            recordingStart = nil
            status = .idle
        } catch {
            fail("Die Aufnahme konnte nicht zur Verarbeitung abgelegt werden: \(error.localizedDescription)")
        }
    }

    /// Bricht die Aufnahme ab und verwirft sie.
    func cancel() {
        if isRecording { stopCapture() }
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
            Log.meeting.error("system audio unavailable, falling back to microphone: \(error.localizedDescription, privacy: .public)")
            return .microphone(uid: settings.inputDeviceUID)
        }
    }

    private func stopCapture() {
        stopElapsedTimer()
        _ = recorder.stop()
        systemAudio.destroy()
    }

    private func fail(_ message: String) {
        discardAudio()
        status = .failed(message)
        Log.meeting.error("meeting failed: \(message, privacy: .public)")
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

    private static var temporaryFolder: URL { AppPaths.meetingRecordings }

    /// Laufende Aufnahme unter ~/Library/Caches – zieht beim Beenden in die Warteschlange.
    private static func temporaryAudioURL() -> URL {
        temporaryFolder.appendingPathComponent("\(UUID().uuidString).wav")
    }

    /// Reste von Aufnahmen entfernen, die beim Absturz oder Beenden der App noch liefen. Beendete
    /// Meetings liegen längst in der Warteschlange (`AppPaths.meetingQueue`) und sind nicht betroffen.
    private static func removeOrphanedRecordings() {
        guard let files = try? FileManager.default.contentsOfDirectory(at: temporaryFolder, includingPropertiesForKeys: nil) else { return }
        for file in files { try? FileManager.default.removeItem(at: file) }
    }
}
