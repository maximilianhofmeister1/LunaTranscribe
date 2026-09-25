import AVFoundation
import Foundation

/// Entwickler-Einstieg für die Hintergrund-Verarbeitung – ohne Mikrofon, deshalb direkt aus dem
/// Terminal startbar:
///
///     say -v Anna -o /tmp/a.wav --data-format=LEF32@16000 "…"
///     LunaTranscribe --queue-selftest /tmp/a.wav [weitere.wav …]
///
/// Kopiert jede Datei als beendetes Meeting in die echte Warteschlange (so, als hätte man mehrere
/// Meetings direkt hintereinander beendet), wartet, bis alle verarbeitet sind, und druckt Reihenfolge,
/// Zeiten und Ergebnis. Gespeichert wird in einen temporären Meeting-Ordner; der eingestellte Ordner
/// wird danach wiederhergestellt. Nutzt die in den Einstellungen gewählte Engine.
@MainActor
enum QueueSelfTest {
    static let flag = "--queue-selftest"

    static var isRequested: Bool { CommandLine.arguments.contains(flag) }

    static func run(state: AppState) {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return }
        let files = args[(index + 1)...].prefix { !$0.hasPrefix("--") }.map { URL(filePath: $0) }
        guard !files.isEmpty else {
            print("Aufruf: --queue-selftest <datei.wav> [weitere.wav …]")
            exit(2)
        }

        Task { @MainActor in
            // Aufräumen (Meeting-Ordner zurücksetzen) muss vor `exit` passieren – `exit` überspringt `defer`.
            let succeeded = await runIsolated(state: state, files: files)
            exit(succeeded ? 0 : 1)
        }
    }

    /// Läuft mit temporärem Meeting-Ordner; stellt den eingestellten Ordner in jedem Fall wieder her.
    private static func runIsolated(state: AppState, files: [URL]) async -> Bool {
        let originalFolder = state.settings.meetingFolder
        let library = FileManager.default.temporaryDirectory.appending(path: "luna-queue-\(UUID().uuidString)")
        defer {
            state.settings.meetingFolder = originalFolder
            try? FileManager.default.removeItem(at: library)
        }
        state.settings.meetingFolder = library.path
        state.meetingStore.reload()

        let started = Date()
        var finished: [String] = []
        let subscription = state.meetingQueue.finished.sink { title, _ in
            finished.append(title)
            print(String(format: "  fertig nach %5.1f s: %@", Date().timeIntervalSince(started), title))
        }
        defer { subscription.cancel() }

        // Alle Meetings direkt hintereinander „beenden“ – jedes mit eigener Aufnahmezeit.
        for (i, file) in files.enumerated() {
            let copy = FileManager.default.temporaryDirectory.appending(path: "luna-queue-\(UUID().uuidString).wav")
            do {
                try FileManager.default.copyItem(at: file, to: copy)
                let audio = try AVAudioFile(forReading: copy)
                let duration = Double(audio.length) / audio.processingFormat.sampleRate
                try state.meetingQueue.enqueue(recording: copy, recordedAt: Date().addingTimeInterval(Double(i * 60)),
                                               durationSeconds: duration, language: state.settings.languageForEngine(state.settings.primaryLanguage),
                                               expectedSpeakers: nil)
            } catch {
                print("FEHLER beim Einreihen von \(file.lastPathComponent): \(error.localizedDescription)")
                return false
            }
        }
        print("\(files.count) Meeting(s) eingereiht nach \(String(format: "%.2f", Date().timeIntervalSince(started))) s – die Aufnahme wäre jetzt wieder frei.")
        state.models.ensureLoaded(state.settings.engine)

        // Warten, bis nichts mehr wartet oder läuft (Fehler zählen als erledigt).
        while state.meetingQueue.pendingCount > 0 {
            try? await Task.sleep(for: .milliseconds(250))
        }

        let failures = state.meetingQueue.items.filter(\.isFailed)
        for item in failures {
            if case .failed(let message) = item.state { print("  FEHLGESCHLAGEN: \(item.job.title): \(message)") }
            state.meetingQueue.discard(item.id)
        }
        print("\nBibliothek:")
        for entry in state.meetingStore.entries {
            let turns = (try? state.meetingStore.load(entry.id))?.turns ?? []
            print("  \(entry.title) – \(turns.count) Beiträge: \(turns.map(\.text).joined(separator: " ").prefix(120))")
        }
        print("\n\(finished.count)/\(files.count) verarbeitet in \(String(format: "%.1f", Date().timeIntervalSince(started))) s")
        return failures.isEmpty && finished.count >= files.count   // >=: wiederaufgenommene Aufträge zählen mit
    }
}
