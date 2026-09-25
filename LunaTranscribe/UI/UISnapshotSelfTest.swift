import AVFoundation
import AppKit
import SwiftUI

/// Entwickler-Einstieg: rendert alle Seiten des Hauptfensters und das Menü-Popover im Hell- und
/// Dunkelmodus als PNG – zum Prüfen des Designs ohne Klicken, auch aus dem Terminal:
///
///     LunaTranscribe --ui-snapshot /tmp/luna-ui
///
/// Für die Meetings-Seite zeigt der Test ein Beispiel-Meeting aus einem temporären Ordner; der
/// eingestellte Meeting-Ordner wird danach wiederhergestellt. Modelle werden nicht geladen.
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
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                exit(1)
            }
        }
    }

    private static func snapshot(state: AppState, into output: URL) async throws {
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

        // Beispiel-Meeting in einem Wegwerf-Ordner, damit Liste und Detail etwas zeigen.
        let originalFolder = state.settings.meetingFolder
        let sampleFolder = FileManager.default.temporaryDirectory.appending(path: "luna-ui-\(UUID().uuidString)")
        defer {
            state.settings.meetingFolder = originalFolder
            try? FileManager.default.removeItem(at: sampleFolder)
        }
        state.settings.meetingFolder = sampleFolder.path
        state.meetingStore.reload()
        if let entry = state.meetingStore.add(sampleMeeting()) { state.router.selectedMeetingID = entry.id }
        state.dictation.injectSampleHistoryForSnapshots()
        // Ein wartender Hintergrund-Auftrag (Modelle sind nicht geladen, er bleibt also „wartend“).
        let queuedID = try enqueueSilentMeeting(state: state)
        defer { if let queuedID { state.meetingQueue.discard(queuedID) } }

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
        }
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
        meeting.summary = MeetingSummary(markdown: """
        ## Kurzfassung
        Der Launch der neuen Version wird auf **Montag** verschoben, weil die Tests noch laufen.

        ## Entscheidungen
        - Launch am Montag

        ## Aufgaben
        - [ ] Markus: Testbericht und weclapp-Export schicken (Freitag)
        """, createdAt: Date(), model: "sonnet")
        return meeting
    }
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
