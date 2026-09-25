import AppKit
import Combine
import KeyboardShortcuts

/// Wurzel der App: besitzt Modelle, Diktat und Meeting und verbindet sie mit Hotkeys, Overlay und
/// Fenstern. Menü und Fenster beobachten nur dieses Objekt – Änderungen der Teile werden durchgereicht.
@MainActor
final class AppState: ObservableObject {
    @Published private(set) var accessibilityGranted = Permissions.accessibilityGranted

    let settings = SettingsStore.shared
    let models = ModelManager()
    /// Sprachmodell für die Nachbearbeitung (dritter Hotkey); wird wie die ASR beim Start geladen.
    let refiners = RefinerManager()
    let dictation: DictationController
    /// Meeting-Modus: eigener Recorder und eigene Statusmaschine, läuft parallel zum Diktat.
    let meeting: MeetingSession
    /// Verarbeitet beendete Meetings im Hintergrund, eines nach dem anderen.
    let meetingQueue: MeetingProcessingQueue
    /// Meeting-Bibliothek im frei wählbaren Meeting-Ordner.
    let meetingStore: MeetingStore
    /// Zusammenfassungen über die Claude CLI.
    let summarizer: MeetingSummarizer
    /// Lernendes Wörterbuch: Vorschläge aus Korrekturen.
    let learner: VocabularyLearner
    /// Suchindex für luna-mcp (Claude): Stichworte und inhaltliche Suche.
    let searchIndexer: SearchIndexer
    /// Navigation des Hauptfensters (Seite, gewähltes Meeting, Einstellungs-Bereich).
    let router = MainRouter()

    private let overlay = OverlayController()
    private let notifier = MeetingNotifier()
    private lazy var mainWindow = MainWindowController(state: self)
    private var escapeMonitor: EscapeKeyMonitor?
    private var cancellables = Set<AnyCancellable>()

    /// - Parameter preloadModels: ASR- und Sprachmodell sofort laden (Normalfall). Selbsttests
    ///   laden ihre Modelle selbst und schalten das ab.
    init(preloadModels: Bool = true) {
        dictation = DictationController(settings: settings, models: models, refiners: refiners)
        meetingStore = MeetingStore(settings: settings)
        meetingQueue = MeetingProcessingQueue(settings: settings, models: models, store: meetingStore)
        meeting = MeetingSession(settings: settings, queue: meetingQueue)
        summarizer = MeetingSummarizer(settings: settings, store: meetingStore)
        learner = VocabularyLearner(settings: settings)
        searchIndexer = SearchIndexer(settings: settings, store: meetingStore, queue: meetingQueue)

        forwardChanges(of: models)
        forwardChanges(of: refiners)
        forwardChanges(of: dictation)
        forwardChanges(of: meeting)
        forwardChanges(of: meetingQueue)
        forwardChanges(of: meetingStore)
        forwardChanges(of: summarizer)
        forwardChanges(of: searchIndexer)
        forwardChanges(of: settings)
        bindModels(preload: preloadModels)
        bindVocabulary()
        bindMeeting()
        registerHotkeys()
        overlay.bind(to: dictation)
        // Escape bricht nur das Diktat ab – ein Meeting wird bewusst nur über das Menü beendet,
        // damit kein versehentlicher Tastendruck eine Stunde Aufnahme verwirft.
        escapeMonitor = EscapeKeyMonitor { [weak self] in self?.dictation.cancel() }
        watchAccessibility()
    }

    // MARK: Fenster

    /// Öffnet das Hauptfenster auf einer Seite (Menüleiste, „Einstellungen…“).
    func openMainWindow(_ page: MainPage? = nil) {
        if let page { router.page = page }
        mainWindow.show()
    }

    func openSettings(_ section: SettingsSection? = nil) {
        if let section { router.settingsSection = section }
        openMainWindow(.settings)
    }

    func openMeeting(_ id: UUID) {
        router.selectedMeetingID = id
        openMainWindow(.meetings)
    }

    // MARK: Verdrahtung

    /// Menü, Menüleisten-Icon und Fenster beobachten nur `AppState`.
    private func forwardChanges<Child: ObservableObject>(of child: Child)
    where Child.ObjectWillChangePublisher == ObservableObjectPublisher {
        child.objectWillChange
            .sink { [weak self] _ in self?.objectWillChange.send() }
            .store(in: &cancellables)
    }

    /// Modelle direkt beim Start laden, damit der erste Hotkey sofort reagiert; bei Wechsel neu laden.
    private func bindModels(preload: Bool) {
        if preload {
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
    }

    /// Wörterbuch an die Engine geben – verzögert, damit nicht jeder Tastendruck in den
    /// Einstellungen eine neue Boosting-Session baut. Der erste Wert kommt sofort.
    private func bindVocabulary() {
        models.updateVocabulary(settings.activeVocabulary)
        settings.$vocabularyEnabled.combineLatest(settings.$vocabularyEntries)
            .dropFirst()
            .debounce(for: .milliseconds(600), scheduler: RunLoop.main)
            .sink { [weak self] enabled, entries in
                self?.models.updateVocabulary(enabled ? CustomVocabulary(entries: entries) : .disabled)
            }
            .store(in: &cancellables)
    }

    private func bindMeeting() {
        // Hintergrund-Verarbeitung meldet sich per Mitteilung statt per Fenster – stört nicht, auch
        // nicht mitten im nächsten Meeting. Ein Klick auf die Mitteilung öffnet das Meeting.
        notifier.onOpenMeeting = { [weak self] id in self?.openMeeting(id) }
        meetingQueue.finished
            .sink { [weak self] title, id in self?.notifier.meetingFinished(title: title, meetingID: id) }
            .store(in: &cancellables)
        meetingQueue.failed
            .sink { [weak self] title, message in self?.notifier.meetingFailed(title: title, message: message) }
            .store(in: &cancellables)
        // Erlaubnis für Mitteilungen erst erfragen, wenn das erste Meeting in die Verarbeitung geht.
        meetingQueue.$items
            .map(\.count)
            .removeDuplicates()
            .filter { $0 > 0 }
            .first()
            .sink { [weak self] _ in self?.notifier.requestAuthorizationIfNeeded() }
            .store(in: &cancellables)
        // Fehler beim Starten/Beenden der Aufnahme als Dialog: das Menü-Popover ist nach dem Klick oft schon zu.
        meeting.$status
            .removeDuplicates()
            .sink { status in
                if case .failed(let message) = status { Self.showMeetingAlert(message) }
            }
            .store(in: &cancellables)
    }

    private func registerHotkeys() {
        let slots: [(KeyboardShortcuts.Name, HotkeySlot)] = [
            (.recordPrimary, .primary), (.recordSecondary, .secondary), (.recordRefined, .refined),
        ]
        for (name, slot) in slots {
            KeyboardShortcuts.onKeyDown(for: name) { [weak self] in self?.dictation.hotkeyDown(slot) }
            KeyboardShortcuts.onKeyUp(for: name) { [weak self] in self?.dictation.hotkeyUp(slot) }
        }
    }

    /// Bedienungshilfen-Status regelmäßig prüfen – er ändert sich in den Systemeinstellungen.
    private func watchAccessibility() {
        if !accessibilityGranted { Permissions.requestAccessibility() }
        Timer.publish(every: 2, on: .main, in: .common)
            .autoconnect()
            .map { _ in Permissions.accessibilityGranted }
            .removeDuplicates()
            .sink { [weak self] granted in self?.accessibilityGranted = granted }
            .store(in: &cancellables)
    }

    private static func showMeetingAlert(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Meeting-Aufnahme fehlgeschlagen"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "OK")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}
