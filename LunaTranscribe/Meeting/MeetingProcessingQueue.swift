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
@MainActor
final class MeetingProcessingQueue: ObservableObject {
    enum State: Equatable {
        case waiting
        case processing(step: String, fraction: Double)
        case failed(String)
    }

    struct Item: Identifiable, Equatable {
        var job: MeetingJob
        var state: State
        var id: UUID { job.id }
    }

    @Published private(set) var items: [Item] = []

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

    static let audioFileName = "audio.wav"
    static let jobFileName = "job.json"

    /// Läuft gerade eine Verarbeitung?
    var isProcessing: Bool { items.contains(where: \.isProcessing) }
    /// Aufträge, die noch verarbeitet werden (laufend oder wartend).
    var pendingCount: Int { items.filter { !$0.isFailed }.count }

    init(settings: SettingsStore, models: ModelManager, store: MeetingStore) {
        self.settings = settings
        self.models = models
        self.store = store
        restore()
        // Beim Start ist die Engine meist noch nicht geladen – sobald sie bereit ist, weitermachen.
        models.$state
            .removeDuplicates()
            .sink { [weak self] state in
                if case .ready = state { Task { @MainActor in self?.processNext() } }
            }
            .store(in: &cancellables)
    }

    // MARK: Aufträge

    /// Übernimmt eine beendete Aufnahme: Datei in die Warteschlange verschieben, Auftrag sichern, loslegen.
    func enqueue(recording: URL, recordedAt: Date, durationSeconds: TimeInterval,
                 language: AppLanguage, expectedSpeakers: Int?) throws {
        let job = MeetingJob(recordedAt: recordedAt, durationSeconds: durationSeconds,
                             language: language, expectedSpeakers: expectedSpeakers)
        let folder = Self.folder(for: job.id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.moveItem(at: recording, to: folder.appending(path: Self.audioFileName))
        try Self.write(job)
        items.append(Item(job: job, state: .waiting))
        Log.meeting.notice("meeting queued: \(durationSeconds, format: .fixed(precision: 0)) s, \(self.items.count) in queue")
        processNext()
    }

    /// Fehlgeschlagenen Auftrag erneut einreihen.
    func retry(_ id: UUID) {
        guard let index = items.firstIndex(where: { $0.id == id }) else { return }
        items[index].job.failure = nil
        items[index].state = .waiting
        try? Self.write(items[index].job)
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
        try? FileManager.default.removeItem(at: Self.folder(for: id))
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
        let audioURL = Self.folder(for: job.id).appending(path: Self.audioFileName)
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
        let folder = Self.folder(for: job.id)
        let audio = folder.appending(path: Self.audioFileName)
        // Mit „Aufnahme behalten“ zieht die WAV in den Meeting-Ordner, sonst wird sie gelöscht.
        let entry = store.add(result, audioURL: settings.meetingKeepAudio ? audio : nil)

        if let entry {
            try? FileManager.default.removeItem(at: folder)
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
        try? Self.write(items[index].job)
        failed.send((job.title, message))
        Log.meeting.error("meeting processing failed: \(message, privacy: .public)")
    }

    /// Fortschritt übernehmen – nur solange der Auftrag noch läuft (verspätete Meldungen nach Abbruch ignorieren).
    private func update(_ id: UUID, _ state: State) {
        guard let index = items.firstIndex(where: { $0.id == id }), case .processing = items[index].state else { return }
        items[index].state = state
    }

    // MARK: Persistenz

    /// Aufträge vom letzten Lauf wieder aufnehmen. Ein beim Beenden laufender Auftrag wartet einfach erneut.
    private func restore() {
        let fm = FileManager.default
        guard let folders = try? fm.contentsOfDirectory(at: AppPaths.meetingQueue, includingPropertiesForKeys: nil) else { return }
        for folder in folders {
            guard let data = try? Data(contentsOf: folder.appending(path: Self.jobFileName)),
                  let job = try? Self.decoder.decode(MeetingJob.self, from: data),
                  fm.fileExists(atPath: folder.appending(path: Self.audioFileName).path) else { continue }
            items.append(Item(job: job, state: job.failure.map(State.failed) ?? .waiting))
        }
        items.sort { $0.job.recordedAt < $1.job.recordedAt }
        if !items.isEmpty { Log.meeting.notice("restored \(self.items.count) queued meeting(s)") }
    }

    private static func folder(for id: UUID) -> URL {
        AppPaths.meetingQueue.appending(path: id.uuidString, directoryHint: .isDirectory)
    }

    private static func write(_ job: MeetingJob) throws {
        try encoder.encode(job).write(to: folder(for: job.id).appending(path: jobFileName), options: .atomic)
    }

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.outputFormatting = [.prettyPrinted, .sortedKeys]
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    private static let decoder: JSONDecoder = {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }()
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
