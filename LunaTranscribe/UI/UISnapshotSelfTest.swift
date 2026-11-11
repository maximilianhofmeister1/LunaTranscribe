import AVFoundation
import AppKit
import LunaCore
import SwiftUI

/// Entwickler-Einstieg: rendert alle Seiten des Hauptfensters und das Menü-Popover im Hell- und
/// Dunkelmodus als PNG – zum Prüfen des Designs ohne Klicken, auch aus dem Terminal:
///
///     LunaTranscribe --ui-snapshot /tmp/luna-ui
///
/// Für die Meetings-Seite zeigt der Test ein Beispiel-Meeting aus einem temporären Ordner, dazu einen wartenden
/// und einen unterbrochenen Auftrag. Er läuft in der `SelfTestSandbox` (eigene Einstellungen, Temp-Warteschlange),
/// echte Einstellungen und Ordner bleiben auch bei einem Absturz unberührt. Modelle werden nicht geladen.
@MainActor
enum UISnapshotSelfTest {
    static let flag = "--ui-snapshot"

    static var isRequested: Bool { CommandLine.arguments.contains(flag) }

    static func run(state: AppState) {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return }
        let output = URL(filePath: args.indices.contains(index + 1) ? args[index + 1] : "/tmp/luna-ui", directoryHint: .isDirectory)

        Task { @MainActor in
            do {
                try await snapshot(state: state, into: output)
                print("UI-Snapshots: \(output.path)")
                SelfTestSandbox.cleanUp()
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                SelfTestSandbox.cleanUp()
                exit(1)
            }
        }
    }

    private static func snapshot(state: AppState, into output: URL) async throws {
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

        // Beispiel-Meeting in einem Wegwerf-Ordner, damit Liste und Detail etwas zeigen. Die Einstellung landet
        // nur in der Sandbox-Suite, der echte Meeting-Ordner bleibt eingestellt.
        let sampleFolder = FileManager.default.temporaryDirectory.appending(path: "luna-ui-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: sampleFolder) }
        state.settings.meetingFolder = sampleFolder.path
        state.meetingStore.reload()
        let sample = state.meetingStore.add(sampleMeeting())
        state.router.selectedMeetingID = sample?.id
        state.dictation.injectSampleHistoryForSnapshots()
        // Ein wartender Hintergrund-Auftrag (Modelle sind nicht geladen, er bleibt also „wartend“).
        let queuedID = try enqueueSilentMeeting(state: state)
        defer { if let queuedID { state.meetingQueue.discard(queuedID) } }
        // Eine gerettete, unterbrochene Aufnahme (wie nach einem Absturz mitten im Meeting).
        let interruptedID = try addInterruptedRecording(state: state)
        defer { state.meetingQueue.discard(interruptedID) }

        for (name, appearance) in [("hell", NSAppearance.Name.aqua), ("dunkel", .darkAqua)] {
            for page in MainPage.allCases {
                state.router.page = page
                try await render(MainView(router: state.router).environmentObject(state), size: NSSize(width: 1100, height: 760),
                                 appearance: appearance, to: output.appending(path: "\(page.rawValue)-\(name).png"))
            }
            state.router.page = .settings
            for section in [SettingsSection.dictation, .meetings] {
                state.router.settingsSection = section
                try await render(MainView(router: state.router).environmentObject(state), size: NSSize(width: 1100, height: 760),
                                 appearance: appearance, to: output.appending(path: "settings-\(section.rawValue)-\(name).png"))
            }
            state.router.settingsSection = .general
            try await render(MenuBarView().environmentObject(state), size: NSSize(width: 320, height: 420),
                             appearance: appearance, titled: false, to: output.appending(path: "menubar-\(name).png"))
            if let sample {
                let document = MeetingDocument(transcript: try state.meetingStore.load(sample.id), store: state.meetingStore,
                                               learner: state.learner, summarizer: state.summarizer)
                let summarySize = NSSize(width: 760, height: 680)
                for (suffix, draft) in [("", nil), ("-bearbeiten", sampleSummary)] as [(String, String?)] {
                    try await render(summarySnapshot(document: document, draft: draft, size: summarySize).environmentObject(state),
                                     size: summarySize, appearance: appearance, titled: false,
                                     to: output.appending(path: "summary\(suffix)-\(name).png"))
                }
            }
        }
        if let sample { try await checkSummaryEditing(meetingID: sample.id, state: state) }
    }

    // MARK: Bearbeiten der Zusammenfassung

    /// Spielt „Bearbeiten → Abbrechen“ und „Bearbeiten → Sichern“ im echten View-Baum durch. Die Knöpfe werden über
    /// ihre Tastenkürzel ausgelöst (⌘E, Esc, ⌘S – die Aktion läuft so aus dem View heraus wie bei einem Klick),
    /// getippt wird per `insertText` in den fokussierten Editor; der Entwurf liegt wie in `MeetingDetailView` in
    /// einem `@State`. (Bedienungshilfen-Klicks gehen nicht: SwiftUI baut den AX-Baum nur für echte AX-Clients.) Hier stürzte 1.2-dev ab: erzwungenes
    /// Entpacken der Entwurfs-Bindung, während der Editor nach „Abbrechen“ abgebaut wurde.
    private static func checkSummaryEditing(meetingID: UUID, state: AppState) async throws {
        let document = MeetingDocument(transcript: try state.meetingStore.load(meetingID), store: state.meetingStore,
                                       learner: state.learner, summarizer: state.summarizer)
        guard let original = document.transcript.summary else { throw SelfTestError("Beispiel-Meeting ohne Zusammenfassung") }
        let size = NSSize(width: 760, height: 680)
        // Titelleiste, damit das Fenster Key werden und der Editor den Fokus bekommen kann.
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled], backing: .buffered, defer: false)
        let host = NSHostingView(rootView: SummaryEditingHarness(document: document).environmentObject(state))
        host.frame = NSRect(origin: .zero, size: size)
        window.contentView = host
        window.makeKeyAndOrderFront(nil)
        defer { window.orderOut(nil) }
        try await settle(host)

        // Bearbeiten, nichts ändern, Abbrechen – der gemeldete Absturz.
        try await press(.edit, in: window)
        try focusEditor(in: window)
        try await press(.cancel, in: window)
        guard host.firstSubview(of: NSTextView.self) == nil else { throw SelfTestError("Abbrechen hat den Editor nicht geschlossen") }

        // Bearbeiten, tippen, Abbrechen: nichts wird gespeichert.
        try await press(.edit, in: window)
        try type("\n- Verworfen", in: window)
        try await settle(host)
        try await press(.cancel, in: window)
        guard try state.meetingStore.load(meetingID).summary == original else { throw SelfTestError("Abbrechen hat gespeichert") }

        // Bearbeiten, tippen, Sichern: Text und Zeitpunkt landen in meeting.json.
        let added = "- Im Selbsttest ergänzt"
        try await press(.edit, in: window)
        try type("\n" + added, in: window)
        try await settle(host)
        try await press(.save, in: window)
        guard host.firstSubview(of: NSTextView.self) == nil else { throw SelfTestError("Sichern hat den Editor nicht geschlossen") }
        let saved = try state.meetingStore.load(meetingID).summary
        guard saved?.markdown.hasSuffix(added) == true, saved?.editedAt != nil else {
            throw SelfTestError("Sichern hat die Zusammenfassung nicht gespeichert")
        }
        print("Zusammenfassung bearbeiten: Abbrechen und Sichern OK")
    }

    private enum Shortcut {
        case edit, cancel, save
    }

    /// Löst einen Knopf über sein Tastenkürzel aus und lässt die Oberfläche nachziehen. ⌘-Kürzel gehen direkt an
    /// `performKeyEquivalent` des Fensters, weil die Test-App nicht aktiv ist; Esc geht als Taste an den Fokus.
    private static func press(_ shortcut: Shortcut, in window: NSWindow) async throws {
        let (characters, keyCode, modifiers): (String, UInt16, NSEvent.ModifierFlags) = switch shortcut {
        case .edit: ("e", 14, .command)
        case .cancel: ("\u{1B}", 53, [])
        case .save: ("s", 1, .command)
        }
        guard let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: modifiers,
                                           timestamp: ProcessInfo.processInfo.systemUptime, windowNumber: window.windowNumber,
                                           context: nil, characters: characters, charactersIgnoringModifiers: characters,
                                           isARepeat: false, keyCode: keyCode) else { throw SelfTestError("Tastenereignis") }
        if modifiers.isEmpty {
            NSApp.sendEvent(event)
        } else if !window.performKeyEquivalent(with: event) {
            throw SelfTestError("Tastenkürzel \(shortcut) hat keinen Knopf ausgelöst")
        }
        guard let host = window.contentView else { return }
        try await settle(host)
    }

    /// Tippt am Ende des Editors – über `insertText` wie die Tastatur, damit SwiftUI die Änderung selbst meldet.
    private static func type(_ text: String, in window: NSWindow) throws {
        try focusEditor(in: window)
        guard let editor = window.firstResponder as? NSTextView else { throw SelfTestError("Editor hat keinen Fokus") }
        editor.setSelectedRange(NSRange(location: (editor.string as NSString).length, length: 0))
        editor.insertText(text, replacementRange: editor.selectedRange())
    }

    private static func focusEditor(in window: NSWindow) throws {
        guard let editor = window.contentView?.firstSubview(of: NSTextView.self), window.makeFirstResponder(editor) else {
            throw SelfTestError("Editor nicht gefunden oder nicht fokussierbar")
        }
    }

    /// Layout und Zeichnen erzwingen, dann kurz warten – so läuft der View-Baum wie nach einem Klick durch.
    private static func settle(_ host: NSView) async throws {
        host.layoutSubtreeIfNeeded()
        host.display()
        try await Task.sleep(for: .milliseconds(250))
        host.layoutSubtreeIfNeeded()
        host.display()
    }

    /// Der Tab „Zusammenfassung“ öffnet erst per Klick, deshalb wird er einzeln gerendert –
    /// einmal zum Lesen, einmal im Bearbeiten (`draft`).
    private static func summarySnapshot(document: MeetingDocument, draft: String?, size: NSSize) -> some View {
        MeetingSummaryTab(document: document, draft: .constant(draft))
            .padding(LunaMetrics.cardPadding)
            .frame(width: size.width, height: size.height, alignment: .top)
            .background(LunaColor.background)
    }

    /// `titled`: Hauptfenster mit (transparenter) Titelleiste; das Popover ist rahmenlos.
    private static func render<V: View>(_ view: V, size: NSSize, appearance: NSAppearance.Name, titled: Bool = true,
                                        to url: URL) async throws {
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                              styleMask: titled ? [.titled, .fullSizeContentView] : [.borderless], backing: .buffered, defer: false)
        window.titlebarAppearsTransparent = true
        window.appearance = NSAppearance(named: appearance)
        let host = NSHostingView(rootView: view)
        host.frame = NSRect(origin: .zero, size: size)
        window.contentView = host
        window.orderFrontRegardless()
        try await Task.sleep(for: .milliseconds(700))   // Layout, Laden, Animationen

        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try rep.representation(using: .png, properties: [:])?.write(to: url)
        window.orderOut(nil)
    }

    /// Legt eine Sekunde Stille als beendetes Meeting in die Warteschlange.
    private static func enqueueSilentMeeting(state: AppState) throws -> UUID? {
        let url = FileManager.default.temporaryDirectory.appending(path: "luna-ui-silence-\(UUID().uuidString).wav")
        let format = AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1)!
        let file = try AVAudioFile(forWriting: url, settings: format.settings)
        let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 16_000)!
        buffer.frameLength = 16_000
        try file.write(from: buffer)
        try state.meetingQueue.enqueue(recording: url, recordedAt: Date(), durationSeconds: 1_834,
                                       language: .german, expectedSpeakers: nil)
        return state.meetingQueue.items.last?.id
    }

    /// Legt einen verwaisten Entwurf (Phase `recording`, Sperre frei, 1 s Stille) in die Warteschlange und
    /// lässt ihn retten – derselbe Weg wie beim Start nach einem Absturz.
    private static func addInterruptedRecording(state: AppState) throws -> UUID {
        let job = MeetingJob(recordedAt: Date().addingTimeInterval(-7_200), durationSeconds: 0, language: .german,
                             expectedSpeakers: nil, phase: .recording)
        let folder = MeetingJob.folder(for: job.id, in: state.meetingQueue.folder)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try job.write(in: state.meetingQueue.folder)
        let writer = try WAVWriter(url: folder.appending(path: MeetingJob.audioFileName), sampleRate: 16_000)
        writer.append([Float](repeating: 0, count: 16_000 * 2_710 / 1_000))
        writer.close()
        state.meetingQueue.recoverInterruptedRecordings()
        return job.id
    }

    private static func sampleMeeting() -> MeetingTranscript {
        let lines: [(String, String)] = [
            ("Sprecher 1", "Guten Morgen zusammen. Heute besprechen wir den Launch der neuen Version von LunaTranscribe."),
            ("Sprecher 2", "Danke. Ich schlage vor, wir verschieben den Launch auf Montag, weil die Tests noch laufen."),
            ("Sprecher 1", "Einverstanden. Kannst du bis Freitag den Testbericht schicken?"),
            ("Sprecher 2", "Mache ich. Den Export aus weclapp hänge ich gleich mit dran."),
        ]
        let turns = lines.enumerated().map { i, line in
            MeetingTranscript.Turn(speakerID: line.0, start: TimeInterval(i * 14), end: TimeInterval(i * 14 + 12), text: line.1)
        }
        var meeting = MeetingTranscript(title: "Launch-Planung v0.2", date: Date().addingTimeInterval(-3600), durationSeconds: 58,
                                        engine: .parakeet, language: .german, turns: turns,
                                        speakerNames: ["Sprecher 1": "Anna", "Sprecher 2": "Markus"])
        meeting.summary = MeetingSummary(markdown: sampleSummary, createdAt: Date().addingTimeInterval(-1800), model: "sonnet",
                                         language: .german, editedAt: Date())
        return meeting
    }

    /// Aufbau wie echte Zusammenfassungen: Oberpunkte mit Unterpunkten, Leerzeile dazwischen, eine
    /// dritte Ebene und ein Punkt, der umbricht – so zeigt der Snapshot die Einrückung vollständig.
    private static let sampleSummary = """
    # Launch-Planung v0.2

    ## Kurzfassung
    Der Launch der neuen Version wird auf **Montag** verschoben, weil die Tests noch laufen.

    ## Themen
    - **Launch-Termin**
      - Die Tests laufen noch, der ursprüngliche Termin ist nicht zu halten
      - Montag ist der früheste Tag, an dem Testbericht und weclapp-Export gemeinsam vorliegen und das Team den Release ohne Wochenendbereitschaft begleiten kann

    - **Testbericht**
      - Soll bis Freitag vorliegen
        - Der weclapp-Export hängt direkt dran

    ## Entscheidungen
    - Launch am Montag

    ## Aufgaben
    - [ ] Markus: Testbericht und weclapp-Export schicken (Freitag)

    ## Offene Fragen
    Keine
    """
}

extension DictationController {
    /// Nur für `--ui-snapshot`: ein paar Beispiel-Diktate in den Verlauf legen.
    func injectSampleHistoryForSnapshots() {
        injectHistory([
            Transcript(date: Date(), text: "Ich schicke dir den Export aus weclapp heute Nachmittag.", language: .german,
                       durationSeconds: 3.2, processingSeconds: 0.4),
            Transcript(date: Date().addingTimeInterval(-600), text: "Also, wir treffen uns morgen um elf Uhr im Büro.", language: .german,
                       durationSeconds: 4.1, processingSeconds: 1.1, rawText: "ähm also wir treffen uns morgen um elf uhr im büro"),
        ])
    }
}

/// Der Tab „Zusammenfassung“ mit dem Entwurf als `@State` – so wie `MeetingDetailView` ihn hält.
private struct SummaryEditingHarness: View {
    @ObservedObject var document: MeetingDocument
    @State private var draft: String?

    var body: some View {
        ScrollView {
            MeetingSummaryTab(document: document, draft: $draft)
                .padding(LunaMetrics.cardPadding)
        }
        .background(LunaColor.background)
    }
}

private struct SelfTestError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

private extension NSView {
    func firstSubview<T: NSView>(of type: T.Type) -> T? {
        for subview in subviews {
            if let match = subview as? T ?? subview.firstSubview(of: type) { return match }
        }
        return nil
    }
}
