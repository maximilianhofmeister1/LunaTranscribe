import Foundation

/// Entwickler-Einstieg ohne UI, um die Meeting-Pipeline (Systemaudio-Tap → Recorder → Diarizer → Engine)
/// reproduzierbar zu prüfen:
///
///     /Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --meeting-selftest 20
///     open -n /Applications/LunaTranscribe.app --args --meeting-selftest 20 --log /tmp/luna.log   # mit App-Berechtigungen
///
/// Nimmt die angegebene Zeit (Sekunden, Standard 15) von Mikrofon + Systemaudio auf – währenddessen
/// z. B. `say -v Anna "…"` abspielen – und druckt das Transkript. Beendet den Prozess danach.
/// Die `open`-Variante ist die verlässliche: Aus einem Terminal gestartet rechnet TCC die Berechtigung
/// „Systemaudio aufnehmen“ dem Terminal zu, und der Tap liefert Stille (Kanalpegel 0,000).
enum MeetingSelfTest {
    /// Druckt jeden Verarbeitungsschritt nur einmal (Progress-Callback kommt von beliebigen Threads).
    private final class StepTracker: @unchecked Sendable {
        private let lock = NSLock()
        private var last = ""
        func update(_ step: String) -> Bool {
            lock.lock(); defer { lock.unlock() }
            guard step != last else { return false }
            last = step
            return true
        }
    }

    static let flag = "--meeting-selftest"

    /// Startet den Selbsttest, falls das Flag gesetzt ist. Gibt `true` zurück, wenn er läuft.
    @MainActor
    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return false }
        let seconds = args.indices.contains(index + 1) ? Double(args[index + 1]) ?? 15 : 15
        // `--log <Pfad>`: Ausgabe in eine Datei, wenn die App über LaunchServices (`open --args …`) startet
        // und kein Terminal hat. Nötig, weil TCC Berechtigungen nach dem startenden Prozess zuordnet.
        if let logIndex = args.firstIndex(of: "--log"), args.indices.contains(logIndex + 1) {
            freopen(args[logIndex + 1], "a", stdout)
            setvbuf(stdout, nil, _IOLBF, 0)
        }

        Task {
            do {
                try await run(seconds: seconds)
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                exit(1)
            }
        }
        return true
    }

    private static func makeSource(tap: SystemAudioTap) -> CaptureSource {
        do {
            let source = try tap.makeCaptureSource(microphoneUID: nil)
            print("Quelle: Mikrofon + Systemaudio, Kanalgruppen \(source.channelGroups ?? []), Aggregate-Eingangskanäle \(source.deviceID.map { AudioDevices.inputChannelCount(of: $0) } ?? -1)")
            return source
        } catch {
            print("Systemaudio nicht verfügbar (\(error.localizedDescription)) – nur Mikrofon")
            return .microphone(uid: nil)
        }
    }

    @MainActor
    private static func run(seconds: Double) async throws {
        let args = CommandLine.arguments
        let tap = SystemAudioTap()
        defer { tap.destroy() }
        let source = makeSource(tap: tap)

        // Datei bleibt liegen, damit man sie bei Problemen anhören kann.
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("luna-selftest-\(UUID().uuidString).wav")

        let recorder = AudioRecorder()
        let t0 = Date()
        recorder.onFirstBuffer = { print("Erster Puffer nach \(String(format: "%.2f", Date().timeIntervalSince(t0))) s") }
        try recorder.start(source: source, sink: .file(url))
        print("Start dauerte \(String(format: "%.2f", Date().timeIntervalSince(t0))) s")
        if let id = source.deviceID {
            try await Task.sleep(for: .seconds(1))
            print("Gerät läuft: \(AudioDevices.isRunning(id))")
        }
        print("Aufnahme läuft \(Int(seconds)) s → \(url.path)")
        print("Eingabegerät liefert \(recorder.inputChannelCount) Kanäle")
        var peakSeen: Float = 0
        for _ in 0..<Int(seconds * 4) {
            try await Task.sleep(for: .milliseconds(250))
            peakSeen = max(peakSeen, recorder.level)
        }
        _ = recorder.stop()
        print("Aufgenommen: \(String(format: "%.1f", recorder.recordedSeconds)) s, Spitzenpegel \(String(format: "%.3f", peakSeen)), je Kanal \(recorder.channelPeaks.map { String(format: "%.3f", $0) })")

        let engine = ParakeetEngine()
        print("Engine wird geladen…")
        try await engine.prepare { _ in }

        let transcriber = MeetingTranscriber(diarizer: SpeakerDiarizer(), engine: engine)
        let lastStep = StepTracker()
        var speakers: Int? = nil
        if let i = args.firstIndex(of: "--speakers"), args.indices.contains(i + 1) { speakers = Int(args[i + 1]) }
        let transcript = try await transcriber.run(audioURL: url, recordedAt: Date(), language: .german,
                                                   expectedSpeakers: speakers) { progress in
            if lastStep.update(progress.step) { print("  \(progress.step)") }
        }
        print("\n" + transcript.render(as: .text))
    }
}
