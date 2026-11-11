import AppKit
import Combine
import Foundation
import LunaCore
import os

/// Aufnahme des Meeting-Modus. Läuft unabhängig vom Diktat (`AppState`): eigener Recorder, eigene
/// Statusmaschine, kein Overlay. Während des Meetings wird nur aufgenommen; „Beenden“ übergibt die
/// Aufnahme an die `MeetingProcessingQueue` und ist sofort wieder bereit für das nächste Meeting.
///
/// Absturzsicher: Die Aufnahme schreibt von Anfang an in einen Auftragsordner der Warteschlange
/// (`beginRecording`), die WAV trägt ihren Kopf alle paar Sekunden nach. Beendet sich die App, fährt
/// der Mac herunter oder schläft er ein, wird ein laufendes Meeting regulär beendet und eingereiht.
@MainActor
final class MeetingSession: ObservableObject {
    enum Status: Equatable {
        case idle
        case recording(since: Date)
        case failed(String)
    }

    /// Wer die Aufnahme beendet. Bei Systemereignissen gibt es keinen Fehlerdialog – er würde das
    /// Beenden bzw. Herunterfahren blockieren.
    private enum StopReason: String {
        case user
        case appTerminating = "app terminating"
        case powerOff = "power off / logout"
        case sleep
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

    /// Entwurf der laufenden Aufnahme in der Warteschlange.
    private var recording: MeetingProcessingQueue.Recording?
    private var elapsedTimer: Timer?
    private var systemObservers: [NSObjectProtocol] = []
    /// Sudden/Automatic Termination ist während der Aufnahme abgeschaltet (muss ausgeglichen bleiben).
    private var terminationGuarded = false

    var isRecording: Bool {
        if case .recording = status { return true }
        return false
    }

    /// Die Aufnahme braucht kein geladenes Modell – die Warteschlange wartet selbst darauf.
    init(settings: SettingsStore, queue: MeetingProcessingQueue) {
        self.settings = settings
        self.queue = queue
        observeSystemEvents()
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
            guard status == .idle || isFailed else { return }   // inzwischen doppelt gestartet

            let draft: MeetingProcessingQueue.Recording
            do {
                // Sprache/Sprecherzahl vom Start – gelten nur, falls die Aufnahme unterbrochen wird.
                draft = try queue.beginRecording(recordedAt: Date(), language: currentLanguage,
                                                 expectedSpeakers: currentSpeakers)
            } catch {
                status = .failed("Die Aufnahme konnte nicht angelegt werden: \(error.localizedDescription)")
                return
            }

            let source = captureSource()
            do {
                try recorder.start(source: source, sink: .file(draft.audioURL))
            } catch {
                systemAudio.destroy()
                queue.discardRecording(draft)
                status = .failed(error.localizedDescription)
                return
            }

            recording = draft
            elapsed = 0
            status = .recording(since: draft.job.recordedAt)
            guardTermination(true)
            startElapsedTimer()
            Log.meeting.notice("meeting recording started, systemAudio=\(self.systemAudioActive)")
        }
    }

    /// Beendet die Aufnahme und reiht sie zur Verarbeitung im Hintergrund ein. Jeder Ausgang ist
    /// sichtbar (Status + Log), damit „Beenden“ nie stumm verpufft.
    func stop() {
        stop(reason: .user)
    }

    private func stop(reason: StopReason) {
        guard isRecording, let draft = recording else { return }
        stopCapture()
        recording = nil

        let duration = recorder.recordedSeconds
        Log.meeting.notice("meeting stopped (\(reason.rawValue, privacy: .public)) after \(duration, format: .fixed(precision: 1)) s (elapsed \(self.elapsed, format: .fixed(precision: 0)) s)")
        guard duration >= RecordingRecovery.minimumDuration else {
            queue.discardRecording(draft)
            if reason == .user {
                fail("Es kam kein Audio an (\(String(format: "%.1f", duration)) s aufgenommen). Mikrofon-/Systemaudio-Berechtigung prüfen.")
            } else {
                status = .idle
            }
            return
        }

        // Kann nicht scheitern: Klappt das Schreiben nicht, bleibt die Aufnahme als unterbrochene erhalten.
        queue.finishRecording(draft, durationSeconds: duration, language: currentLanguage, expectedSpeakers: currentSpeakers)
        status = .idle
    }

    /// Bricht die Aufnahme ab und verwirft sie.
    func cancel() {
        if isRecording { stopCapture() }
        if let draft = recording { queue.discardRecording(draft) }
        recording = nil
        status = .idle
    }

    func dismissError() {
        if isFailed { status = .idle }
    }

    // MARK: Systemereignisse

    /// Beenden, Herunterfahren/Abmelden und Ruhezustand beenden ein laufendes Meeting regulär – schnell
    /// und ohne Dialog. Alles läuft synchron auf dem Main Thread, bevor die Benachrichtigung zurückkehrt.
    /// Im Ruhezustand liefern Mikrofon und Tap ohnehin nichts, danach sind die Geräte oft neu verbunden.
    private func observeSystemEvents() {
        let events: [(NotificationCenter, Notification.Name, StopReason)] = [
            (.default, NSApplication.willTerminateNotification, .appTerminating),
            (NSWorkspace.shared.notificationCenter, NSWorkspace.willPowerOffNotification, .powerOff),
            (NSWorkspace.shared.notificationCenter, NSWorkspace.willSleepNotification, .sleep),
        ]
        systemObservers = events.map { center, name, reason in
            center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.stop(reason: reason) }
            }
        }
    }

    /// Während der Aufnahme darf macOS die App nicht still beenden (Sudden/Automatic Termination),
    /// sonst kämen die Benachrichtigungen oben nie an.
    private func guardTermination(_ on: Bool) {
        guard on != terminationGuarded else { return }
        terminationGuarded = on
        let info = ProcessInfo.processInfo
        if on {
            info.disableSuddenTermination()
            info.disableAutomaticTermination("Meeting-Aufnahme läuft")
        } else {
            info.enableSuddenTermination()
            info.enableAutomaticTermination("Meeting-Aufnahme läuft")
        }
    }

    // MARK: Intern

    private var isFailed: Bool {
        if case .failed = status { return true }
        return false
    }

    private var currentLanguage: AppLanguage { settings.languageForEngine(settings.primaryLanguage) }
    private var currentSpeakers: Int? { settings.meetingSpeakerCount > 0 ? settings.meetingSpeakerCount : nil }

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

    /// Stoppt Recorder (WAV wird mit vollständigem Kopf geschlossen) und Tap.
    private func stopCapture() {
        stopElapsedTimer()
        _ = recorder.stop()
        systemAudio.destroy()
        guardTermination(false)
    }

    private func fail(_ message: String) {
        status = .failed(message)
        Log.meeting.error("meeting failed: \(message, privacy: .public)")
    }

    private func startElapsedTimer() {
        elapsedTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, case .recording(let since) = self.status else { return }
                self.elapsed = Date().timeIntervalSince(since)
            }
        }
    }

    private func stopElapsedTimer() {
        elapsedTimer?.invalidate()
        elapsedTimer = nil
    }
}
