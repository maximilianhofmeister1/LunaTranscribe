import Combine
import Foundation
import LunaCore

/// Hält den Suchindex für luna-mcp aktuell, solange Claude Meetings lesen darf: Stichwort-Teil nach jeder
/// Änderung der Bibliothek, Vektoren für die inhaltliche Suche im Hintergrund.
///
/// Warum in der App: Die App hat das Modell ohnehin (Download, GPU) und kennt jede Änderung sofort. luna-mcp
/// rechnet nur nach, was fehlt – etwa wenn die App nicht läuft. Beide nutzen `SearchIndex.sync`, das ist
/// idempotent; SQLite (WAL) regelt den gleichzeitigen Zugriff.
///
/// Rücksicht: Vektoren werden nicht berechnet, während ein Meeting verarbeitet wird (GPU/Neural Engine
/// und Speicher gehören dann der Transkription); danach wird das Modell wieder entladen.
@MainActor
final class SearchIndexer: ObservableObject {
    enum ModelState: Equatable {
        case off
        case notDownloaded
        case downloading(Double)
        case ready
        case failed(String)
    }

    @Published private(set) var modelState: ModelState = .off
    @Published private(set) var stats: SearchIndex.Stats?
    @Published private(set) var isIndexing = false

    private let settings: SettingsStore
    private let store: MeetingStore
    private let queue: MeetingProcessingQueue
    private let kind = EmbeddingModelKind.standard
    private var index: SearchIndex?
    private var embedder: MLXTextEmbedder?
    private var runTask: Task<Void, Never>?
    private var runAgain = false
    private var cancellables = Set<AnyCancellable>()

    /// Höchstens so viele Abschnitte je Durchgang, dann wird die Warteschlange erneut geprüft.
    private static let embedBatch = 64

    init(settings: SettingsStore, store: MeetingStore, queue: MeetingProcessingQueue) {
        self.settings = settings
        self.store = store
        self.queue = queue
        updateModelState()

        // Verzögert: Tippen im Titel soll nicht bei jedem Zeichen neu indexieren.
        store.$entries
            .dropFirst()
            .debounce(for: .seconds(3), scheduler: RunLoop.main)
            .sink { [weak self] _ in self?.scheduleRun() }
            .store(in: &cancellables)
        settings.$mcpAccessEnabled.combineLatest(settings.$semanticSearchEnabled)
            .dropFirst()
            .removeDuplicates { $0 == $1 }
            .sink { [weak self] _, _ in self?.configurationChanged() }
            .store(in: &cancellables)
        // Nach einer Meeting-Verarbeitung nachholen, was währenddessen liegen geblieben ist.
        queue.$items
            .map { $0.contains(where: \.isProcessing) }
            .removeDuplicates()
            .filter { !$0 }
            .sink { [weak self] _ in self?.scheduleRun() }
            .store(in: &cancellables)
        scheduleRun()
    }

    // MARK: Steuerung

    func scheduleRun() {
        guard settings.mcpAccessEnabled else { return }
        guard runTask == nil else {
            runAgain = true
            return
        }
        runTask = Task { [weak self] in
            await self?.run()
            self?.finishRun()
        }
    }

    /// Lädt das Embedding-Modell (einmalig, ~470 MB) und indexiert danach.
    func downloadModel() {
        if case .downloading = modelState { return }
        modelState = .downloading(0)
        let embedder = MLXTextEmbedder(kind: kind, offlineOnly: false)
        Task { [weak self] in
            do {
                try await embedder.prepare { fraction in
                    Task { @MainActor in
                        guard let self, case .downloading = self.modelState else { return }
                        self.modelState = .downloading(fraction)
                    }
                }
                self?.embedder = embedder
                self?.modelState = .ready
                Log.search.notice("embedding model ready")
                self?.scheduleRun()
            } catch {
                self?.modelState = .failed(error.localizedDescription)
                Log.search.error("embedding model failed: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// Löscht den Suchindex; er wird beim nächsten Durchgang (oder von luna-mcp) neu aufgebaut.
    func deleteIndex() {
        runTask?.cancel()
        index = nil
        stats = nil
        let url = AppPaths.searchIndex
        for suffix in ["", "-wal", "-shm"] {
            try? FileManager.default.removeItem(at: URL(filePath: url.path + suffix))
        }
        Log.search.notice("search index deleted")
    }

    // MARK: Ablauf

    private func configurationChanged() {
        updateModelState()
        if settings.semanticSearchEnabled, settings.mcpAccessEnabled, modelState == .notDownloaded {
            downloadModel()
        } else if !settings.semanticSearchEnabled {
            let loaded = embedder
            embedder = nil
            Task { await loaded?.unload() }
        }
        scheduleRun()
    }

    private func updateModelState() {
        if case .downloading = modelState { return }
        guard settings.semanticSearchEnabled else { modelState = .off; return }
        modelState = kind.isDownloaded ? .ready : .notDownloaded
    }

    private func run() async {
        isIndexing = true
        defer { isIndexing = false }
        let folder = store.libraryFolder
        let meetings = await Task.detached(priority: .utility) {
            MeetingStore.storedMeetings(libraryFolder: folder)
        }.value

        do {
            let index = try self.index ?? SearchIndex(url: AppPaths.searchIndex)
            self.index = index
            let report = try await index.sync(meetings)
            if report.hasChanges {
                Log.search.notice("index synced: +\(report.added) ~\(report.updated) -\(report.removed)")
            }
            if settings.semanticSearchEnabled, let embedder = readyEmbedder() {
                while !queue.isProcessing, !Task.isCancelled {
                    let done = try await index.embedPending(with: embedder, maxChunks: Self.embedBatch)
                    if done == 0 { break }
                }
                if queue.isProcessing { runAgain = true }
                // Die Anfragen rechnet luna-mcp; die App braucht das Modell erst beim nächsten Meeting wieder.
                await embedder.unload()
            }
            stats = try await index.stats(model: settings.semanticSearchEnabled ? kind.huggingFaceID : nil)
        } catch {
            Log.search.error("indexing failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private func finishRun() {
        runTask = nil
        if runAgain {
            runAgain = false
            scheduleRun()
        }
    }

    private func readyEmbedder() -> MLXTextEmbedder? {
        guard kind.isDownloaded else {
            modelState = .notDownloaded
            return nil
        }
        if embedder == nil { embedder = MLXTextEmbedder(kind: kind, offlineOnly: true) }
        modelState = .ready
        return embedder
    }
}
