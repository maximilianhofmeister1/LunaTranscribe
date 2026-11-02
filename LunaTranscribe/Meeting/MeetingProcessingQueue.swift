import Combine
import Foundation
import LunaCore

/// Verarbeitet beendete Meetings im Hintergrund, eines nach dem anderen: Sprecher erkennen,
/// transkribieren, in der Bibliothek ablegen. Die Aufnahme ist dadurch sofort wieder frei für das
/// nächste Meeting.
///
/// Nacheinander statt parallel, weil sich Diarizer und Engine sonst um Neural Engine und RAM streiten
/// und beide Meetings langsamer fertig würden. Invariante: **eine Aufnahme geht nie verloren** – sie
/// wird erst gelöscht, wenn das Transkript gespeichert ist oder der Nutzer sie verwirft.
///
/// Schon während der Aufnahme liegt das Meeting als Auftrag in Phase `recording` hier (`beginRecording`),
/// gesichert durch eine `RecordingLock`. Stürzt die App ab oder wird der Mac mit laufendem Meeting
/// ausgeschaltet, findet `RecordingRecovery` den Auftrag beim nächsten Start, repariert die WAV und
/// zeigt ihn als „Unterbrochene Aufnahme“ – verarbeitet wird er erst auf Klick.
@MainActor
final class MeetingProcessingQueue: ObservableObject {
    enum State: Equatable {
        case waiting
        case processing(step: String, fraction: Double)
        case failed(String)
        /// Gerettete Aufnahme nach Absturz/Herunterfahren; wartet auf „Transkription starten“.
        case interrupted
    }

    /// Eine laufende Aufnahme: Auftragsordner mit `job.json` (Phase `recording`) und gehaltener Sperre.
    struct Recording {
        let job: MeetingJob
        let audioURL: URL
        fileprivate let lock: RecordingLock
    }

    enum QueueError: LocalizedError {
        case lockUnavailable

        var errorDescription: String? { "Die Aufnahme konnte nicht vorbereitet werden (Sperrdatei nicht verfügbar)." }
    }

    struct Item: Identifiable, Equatable {
        var job: MeetingJob
        var state: State
        var id: UUID { job.id }
    }

    @Published private(set) var items: [Item] = []
    /// Beim Start gerettete, unterbrochene Aufnahmen (für die Mitteilung).
    private(set) var recoveredAtLaunch: [MeetingJob] = []

    /// Meldet ein fertig gespeichertes Meeting (Bibliotheks-ID) für die Mitteilung.
    let finished = PassthroughSubject<(title: String, meetingID: UUID), Never>()
    /// Meldet einen fehlgeschlagenen Auftrag für die Mitteilung.
    let failed = PassthroughSubject<(title: String, message: String), Never>()

    private let settings: SettingsStore
    private let models: ModelManager
    private let store: MeetingStore
    /// Bleibt zwischen Meetings geladen (Modelle sind klein), spart die Kompilierung beim nächsten Mal.
    private let diarizer = SpeakerDiarizer()
    private var runningTask: Task<Void, Never>?
    private var cancellables = Set<AnyCancellable>()
    /// Ordner der Warteschlange (Selbsttests nutzen einen temporären).
    let folder: URL

    /// Läuft gerade eine Verarbeitung?
    var isProcessing: Bool { items.contains(where: \.isProcessing) }
    /// Aufträge, die noch verarbeitet werden (laufend oder wartend).
    var pendingCount: Int { items.filter { $0.state == .waiting || $0.isProcessing }.count }
    /// Gerettete Aufnahmen, die auf eine Entscheidung warten.
    var interruptedCount: Int { items.filter { $0.state == .interrupted }.count }

    /// - Parameters:
    ///   - folder: Warteschlangen-Ordner.
    ///   - legacyRecordings: alte Aufnahmen-Ablage (bis 1.1.0), deren Reste beim Start gerettet werden.
    init(settings: SettingsStore, models: ModelManager, store: MeetingStore,
         folder: URL = AppPaths.meetingQueue, legacyRecordings: URL? = AppPaths.meetingRecordings) {
        self.settings = settings
        self.models = models
        self.store = store
        self.folder = folder
        restore(legacyRecordings: legacyRecordings)
        // Beim Start ist die Engine meist noch nicht geladen – sobald sie bereit ist, weitermachen.
        models.$state
            .removeDuplicates()
            .sink { [weak self] state in
                if case .ready = state { Task { @MainActor in self?.processNext() } }
            }
            .store(in: &cancellables)
    }

    // MARK: Aufnahme

    /// Legt den Auftrag für ein neues Meeting an, bevor das erste Sample geschrieben wird: Ordner,
    /// Sperre, `job.json` in Phase `recording`. Die Aufnahme schreibt direkt nach `audioURL`.
    func beginRecording(recordedAt: Date, language: AppLanguage, expectedSpeakers: Int?) throws -> Recording {
        let job = MeetingJob(recordedAt: recordedAt, durationSeconds: 0, language: language,
                             expectedSpeakers: expectedSpeakers, phase: .recording)
        let dir = jobFolder(for: job.id)
        let fm = FileManager.default
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        // Erst die Sperre, dann job.json – sonst könnte eine zweite Instanz den Entwurf für verwaist halten.
        guard let lock = RecordingLock.acquire(at: dir.appending(path: MeetingJob.lockFileName)) else {
            try? fm.removeItem(at: dir)
            throw QueueError.lockUnavailable
        }
        do {
            try job.write(in: folder)
        } catch {
            lock.release()
            try? fm.removeItem(at: dir)
            throw error
        }
        return Recording(job: job, audioURL: dir.appending(path: MeetingJob.audioFileName), lock: lock)
    }

    /// Aufnahme ist beendet: Auftrag freigeben und einreihen. Sprache und Sprecherzahl werden jetzt
    /// festgelegt. Scheitert das Schreiben, bleibt die Aufnahme als unterbrochene erhalten – nie verloren.
    func finishRecording(_ recording: Recording, durationSeconds: TimeInterval,
                         language: AppLanguage, expectedSpeakers: Int?) {
        var job = recording.job
        job.durationSeconds = durationSeconds
        job.language = language
        job.expectedSpeakers = expectedSpeakers
        job.phase = nil
        do {
            try job.write(in: folder)
            recording.lock.release(removeFile: true)
            items.append(Item(job: job, state: .waiting))
            Log.meeting.notice("meeting queued: \(durationSeconds, format: .fixed(precision: 0)) s, \(self.items.count) in queue")
            processNext()
        } catch {
            // job.json sagt weiter `recording`, die Sperre ist frei → spätestens der nächste Start rettet sie.
            recording.lock.release()
            job.phase = .interrupted
            items.append(Item(job: job, state: .interrupted))
            Log.meeting.error("meeting could not be queued, kept as interrupted: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// Aufnahme abgebrochen (Nutzer oder kein Audio): Entwurf samt Datei entfernen.
    func discardRecording(_ recording: Recording) {
        recording.lock.release()
        try? FileManager.default.removeItem(at: jobFolder(for: recording.job.id))
    }

    // MARK: Aufträge

    /// Übernimmt eine fertige Aufnahme von außen (Selbsttests): Datei in die Warteschlange verschieben,
    /// Auftrag sichern, loslegen.
    func enqueue(recording: URL, recordedAt: Date, durationSeconds: TimeInterval,
                 language: AppLanguage, expectedSpeakers: Int?) throws {
        let job = MeetingJob(recordedAt: recordedAt, durationSeconds: durationSeconds,
                             language: language, expectedSpeakers: expectedSpeakers)
        let dir = jobFolder(for: job.id)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: recording, to: dir.appending(path: MeetingJob.audioFileName))
        try job.write(in: folder)
        items.append(Item(job: job, state: .waiting))
        Log.meeting.notice("meeting queued: \(durationSeconds, format: .fixed(precision: 0)) s, \(self.items.count) in queue")
        processNext()
    }

    /// Fehlgeschlagenen Auftrag erneut einreihen bzw. eine gerettete Aufnahme zur Transkription freigeben.
    func retry(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }), !items[index].isProcessing else { return }
        items[index].job.failure = nil
        items[index].job.phase = nil
        items[index].state = .waiting
        // Scheitert das Schreiben, läuft die Verarbeitung trotzdem; die Datei bleibt bis zum Speichern liegen.
        try? items[index].job.write(in: folder)
        processNext()
    }

    /// Auftrag samt Aufnahme endgültig verwerfen – bricht eine laufende Verarbeitung ab.
    func discard(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        if case .processing = items[index].state {
            runningTask?.cancel()
            runningTask = nil
        }
        items.remove(at: index)
        try? FileManager.default.removeItem(at: jobFolder(for: id))
        Log.meeting.notice("meeting job discarded")
        processNext()
    }

    // MARK: Ablauf

    /// Startet den nächsten wartenden Auftrag, falls gerade keiner läuft und die Engine bereit ist.
    private func processNext() {
        guard runningTask == nil,
              let index = items.firstIndex(where: { $0.state == .waiting }),
              let engine = models.current else { return }

        let job = items[index].job
        items[index].state = .processing(step: "Verarbeitung startet…", fraction: 0)
        let audioURL = jobFolder(for: job.id).appending(path: MeetingJob.audioFileName)
        let transcriber = MeetingTranscriber(diarizer: diarizer, engine: engine)

        runningTask = Task.detached(priority: .utility) { [weak self] in
            do {
                let result = try await transcriber.run(audioURL: audioURL, recordedAt: job.recordedAt, language: job.language,
                                                       expectedSpeakers: job.expectedSpeakers) { progress in
                    Task { @MainActor in self?.update(job.id, .processing(step: progress.step, fraction: progress.fraction)) }
                }
                await self?.finish(job, with: result)
            } catch is CancellationError {
                // Verworfen: Aufräumen hat `discard` schon erledigt.
            } catch {
                await self?.fail(job, message: error.localizedDescription)
            }
        }
    }

    private func finish(_ job: MeetingJob, with result: MeetingTranscript) {
        runningTask = nil
        let dir = jobFolder(for: job.id)
        let audio = dir.appending(path: MeetingJob.audioFileName)
        // Mit „Aufnahme behalten“ zieht die WAV in den Meeting-Ordner, sonst wird sie gelöscht.
        let entry = store.add(result, audioURL: settings.meetingKeepAudio ? audio : nil)

        if let entry {
            try? FileManager.default.removeItem(at: dir)
            items.removeAll { $0.id == job.id }
            finished.send((entry.title, entry.id))
            Log.meeting.notice("meeting processed: \(result.turns.count) turns, saved=\(entry.folder.lastPathComponent, privacy: .public)")
        } else {
            // Speichern ist gescheitert – Aufnahme behalten, damit ein neuer Versuch möglich ist.
            markFailed(job, message: "Das Transkript konnte nicht gespeichert werden. Meeting-Ordner prüfen und erneut versuchen.")
        }
        processNext()
    }

    private func fail(_ job: MeetingJob, message: String) {
        runningTask = nil
        markFailed(job, message: message)
        processNext()
    }

    private func markFailed(_ job: MeetingJob, message: String) {
        guard let index = items.firstIndex(where: { $0.id == job.id }) else { return }
        items[index].job.failure = message
        items[index].state = .failed(message)
        try? items[index].job.write(in: folder)
        failed.send((job.title, message))
        Log.meeting.error("meeting processing failed: \(message, privacy: .public)")
    }

    /// Fortschritt übernehmen – nur solange der Auftrag noch läuft (verspätete Meldungen nach Abbruch ignorieren).
    private func update(_ id: UUID, _ state: State) {
        guard let index = items.firstIndex(where: { $0.id == id }), case .processing = items[index].state else { return }
        items[index].state = state
    }

    // MARK: Persistenz

    /// Aufträge vom letzten Lauf wieder aufnehmen. Ein beim Beenden laufender Auftrag wartet einfach erneut;
    /// eine beim Beenden laufende Aufnahme wird vorher gerettet.
    private func restore(legacyRecordings: URL?) {
        recoveredAtLaunch = runRecovery(legacyRecordings: legacyRecordings).recovered
        for job in MeetingJob.readAll(in: folder) {
            switch job.phase {
            case .recording: continue   // läuft gerade in einem anderen Prozess (Sperre gehalten)
            case .interrupted: items.append(Item(job: job, state: .interrupted))
            case nil: items.append(Item(job: job, state: job.failure.map(State.failed) ?? .waiting))
            }
        }
        if !items.isEmpty { Log.meeting.notice("restored \(self.items.count) queued meeting(s)") }
    }

    /// Sucht verwaiste Aufnahmen (Sperre frei) und zeigt sie als unterbrochen an. Sicher jederzeit
    /// aufrufbar – die eigene laufende Aufnahme hält ihre Sperre und bleibt unberührt.
    @discardableResult
    func recoverInterruptedRecordings() -> [MeetingJob] {
        let recovered = runRecovery(legacyRecordings: nil).recovered
        for job in recovered where !items.contains(where: { $0.id == job.id }) {
            items.append(Item(job: job, state: .interrupted))
        }
        items.sort { $0.job.recordedAt < $1.job.recordedAt }
        return recovered
    }

    private func runRecovery(legacyRecordings: URL?) -> RecordingRecovery.Report {
        let report = RecordingRecovery.run(queueFolder: folder, legacyFolder: legacyRecordings,
                                           legacyLanguage: settings.languageForEngine(settings.primaryLanguage))
        if !report.recovered.isEmpty || report.discardedEmpty > 0 || !report.unrepairable.isEmpty {
            Log.meeting.notice("recovery: \(report.recovered.count) interrupted recording(s) rescued, \(report.discardedEmpty) empty draft(s) removed, \(report.skippedActive) active, unrepairable: \(report.unrepairable, privacy: .public)")
        }
        return report
    }

    private func jobFolder(for id: UUID) -> URL {
        MeetingJob.folder(for: id, in: folder)
    }
}

extension MeetingProcessingQueue.Item {
    var isProcessing: Bool {
        if case .processing = state { return true }
        return false
    }

    var isFailed: Bool {
        if case .failed = state { return true }
        return false
    }
}
