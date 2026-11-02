import AVFoundation
import Foundation
import LunaCore

/// Entwickler-Einstieg für die Rettung abgebrochener Meeting-Aufnahmen – ohne Mikrofon, deshalb
/// direkt aus dem Terminal startbar:
///
///     LunaTranscribe --recovery-selftest
///
/// Baut in einem temporären Warteschlangen-Ordner nach, was ein Absturz mitten im Meeting hinterlässt:
/// einen Entwurf mit veraltetem WAV-Kopf und angefangenem Frame (wie `WAVWriter` ihn zurücklässt), eine
/// lose WAV der alten Ablage mit Kopfgröße 0 (Apple-`AVAudioFile`, Stand 1.1.0), einen sehr kurzen
/// Entwurf und eine noch laufende Aufnahme (Sperre gehalten). Dann startet eine echte
/// `MeetingProcessingQueue` auf diesem Ordner und es wird geprüft: Dauer aus der Dateigröße, WAV mit
/// AVFoundation lesbar und inhaltlich unverändert, nichts automatisch verarbeitet, die laufende
/// Aufnahme unberührt, „Transkription starten“ und der reguläre Ablauf Start → Beenden.
@MainActor
enum RecoverySelfTest {
    static let flag = "--recovery-selftest"

    static var isRequested: Bool { CommandLine.arguments.contains(flag) }

    private static let rate = 16_000

    static func run(state: AppState) {
        guard isRequested else { return }
        Task { @MainActor in
            let root = FileManager.default.temporaryDirectory.appending(path: "luna-recovery-\(UUID().uuidString)", directoryHint: .isDirectory)
            let failures = runChecks(state: state, root: root)
            try? FileManager.default.removeItem(at: root)   // vor `exit` – das überspringt `defer`
            print(failures == 0 ? "\nAlle Prüfungen bestanden." : "\n\(failures) Prüfung(en) fehlgeschlagen.")
            exit(failures == 0 ? 0 : 1)
        }
    }

    private static func runChecks(state: AppState, root: URL) -> Int {
        var failures = 0
        func check(_ ok: Bool, _ label: String) {
            print("\(ok ? "OK    " : "FEHLER") \(label)")
            if !ok { failures += 1 }
        }

        let queueFolder = root.appending(path: "Queue", directoryHint: .isDirectory)
        let legacyFolder = root.appending(path: "Meetings", directoryHint: .isDirectory)
        do {
            try FileManager.default.createDirectory(at: queueFolder, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: legacyFolder, withIntermediateDirectories: true)

            // 1. Absturz mitten in der Aufnahme: letzter Kopf-Flush nach 3 s, danach weitere 2,3 s + halbes Frame.
            let crashed = try makeDraft(in: queueFolder)
            let crashedAudio = MeetingJob.folder(for: crashed.id, in: queueFolder).appending(path: MeetingJob.audioFileName)
            do {
                let writer = try WAVWriter(url: crashedAudio, sampleRate: rate, flushInterval: 3600)
                writer.append(signal(frames: 3 * rate))
                writer.flush()
                writer.append(signal(frames: 2 * rate + rate * 3 / 10, offset: 3 * rate))
                _ = writer.writtenBytes   // wartet, bis alles geschrieben ist – danach „stirbt“ der Prozess ohne close()
            }
            try appendBytes([0x12, 0x34, 0x56], to: crashedAudio)
            let broken = try WAVFile.inspect(at: crashedAudio)
            check(abs(broken.durationSeconds - 5.3) < 0.001 && broken.declaredDataBytes == 3 * rate * 4,
                  "Entwurf nach Absturz: Kopf veraltet (3 s), Daten 5,3 s")

            // 2. Rest der alten Ablage (1.1.0, AVAudioFile), Kopfgrößen 0.
            let legacyAudio = legacyFolder.appending(path: "\(UUID().uuidString).wav")
            try writeWithAVAudioFile(legacyAudio, frames: 4 * rate)
            try zeroSizes(legacyAudio)
            // Älter als die Ruhezeit, sonst gilt sie als noch in Arbeit (alte Version könnte parallel aufnehmen).
            try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-3_600)], ofItemAtPath: legacyAudio.path)

            // 3. Sehr kurzer Entwurf (0,3 s) – wird still verworfen.
            let short = try makeDraft(in: queueFolder)
            let shortWriter = try WAVWriter(url: MeetingJob.folder(for: short.id, in: queueFolder).appending(path: MeetingJob.audioFileName),
                                            sampleRate: rate)
            shortWriter.append(signal(frames: rate * 3 / 10))
            shortWriter.close()

            // 4. Läuft noch (z. B. in der „anderen“ Instanz): Sperre gehalten.
            let active = try makeDraft(in: queueFolder)
            let activeFolder = MeetingJob.folder(for: active.id, in: queueFolder)
            let activeLock = RecordingLock.acquire(at: activeFolder.appending(path: MeetingJob.lockFileName))
            let activeWriter = try WAVWriter(url: activeFolder.appending(path: MeetingJob.audioFileName), sampleRate: rate)
            activeWriter.append(signal(frames: 2 * rate))
            activeWriter.flush()
            check(activeLock != nil, "Sperre der laufenden Aufnahme gehalten")

            // Start der App: Warteschlange auf dem Temp-Ordner, ihr init rettet.
            let t0 = Date()
            let queue = MeetingProcessingQueue(settings: state.settings, models: state.models, store: state.meetingStore,
                                               folder: queueFolder, legacyRecordings: legacyFolder)
            print(String(format: "       Rettung beim Start dauerte %.0f ms", Date().timeIntervalSince(t0) * 1000))

            let interrupted = queue.items.filter { $0.state == .interrupted }
            check(queue.recoveredAtLaunch.count == 2 && interrupted.count == 2 && queue.items.count == 2,
                  "2 unterbrochene Aufnahmen gerettet (gefunden: \(queue.items.map { "\($0.state)" }))")
            check(queue.pendingCount == 0 && !queue.isProcessing, "nichts automatisch verarbeitet")
            check(!FileManager.default.fileExists(atPath: MeetingJob.folder(for: short.id, in: queueFolder).path), "kurzer Entwurf verworfen")
            check((try? MeetingJob.read(folder: activeFolder))?.phase == .recording, "laufende Aufnahme unberührt")
            check((try? FileManager.default.contentsOfDirectory(atPath: legacyFolder.path))?.isEmpty == true, "alte Ablage übernommen")

            if let item = queue.items.first(where: { $0.id == crashed.id }) {
                check(abs(item.job.durationSeconds - 5.3) < 0.001, "Dauer aus Dateigröße: \(item.job.durationSeconds) s")
                check(try verifyReadable(crashedAudio, frames: 5 * rate + rate * 3 / 10), "repariertes WAV mit AVFoundation lesbar, Inhalt unverändert")
                check(item.job.expectedSpeakers == 2 && item.job.language == .german, "Metadaten vom Start erhalten")
            } else {
                check(false, "Entwurf nach Absturz in der Warteschlange")
            }
            if let legacy = queue.items.first(where: { $0.id != crashed.id }) {
                let audio = MeetingJob.folder(for: legacy.id, in: queueFolder).appending(path: MeetingJob.audioFileName)
                check(abs(legacy.job.durationSeconds - 4) < 0.001, "alte Aufnahme: Dauer \(legacy.job.durationSeconds) s")
                check(try verifyReadable(audio, frames: 4 * rate), "alte Aufnahme (Apple-Kopf mit Füllabschnitten) lesbar")
            }

            // Die laufende Aufnahme „stirbt“: ein erneuter Suchlauf (z. B. nächster Start) findet sie jetzt.
            activeWriter.append(signal(frames: rate))
            _ = activeWriter.writtenBytes
            activeLock?.release()
            let later = queue.recoverInterruptedRecordings()
            check(later.map(\.id) == [active.id] && queue.interruptedCount == 3, "Aufnahme nach Prozessende gerettet")
            check(later.first.map { abs($0.durationSeconds - 3) < 0.001 } == true, "Dauer inkl. Daten nach dem letzten Flush")

            // „Transkription starten“: wartet jetzt (Modelle sind im Selbsttest nicht geladen).
            queue.retry(crashed.id)
            check(queue.items.first { $0.id == crashed.id }?.state == .waiting, "Transkription starten → wartet")
            check((try? MeetingJob.read(folder: MeetingJob.folder(for: crashed.id, in: queueFolder)))?.phase == nil, "job.json: Phase entfernt")

            // Regulärer Ablauf: Start → Aufnahme → Beenden.
            let recording = try queue.beginRecording(recordedAt: Date(), language: .german, expectedSpeakers: nil)
            let recordingFolder = recording.audioURL.deletingLastPathComponent()
            let writer = try WAVWriter(url: recording.audioURL, sampleRate: rate)
            check(MeetingJob.readAll(in: queueFolder).first { $0.id == recording.job.id }?.isRecordingActive(in: queueFolder) == true,
                  "Entwurf liegt während der Aufnahme gesperrt in der Warteschlange")
            writer.append(signal(frames: 2 * rate))
            writer.close()
            check(queue.recoverInterruptedRecordings().isEmpty, "eigene laufende Aufnahme wird nicht für verwaist gehalten")
            queue.finishRecording(recording, durationSeconds: 2, language: .english, expectedSpeakers: 3)
            let finished = try? MeetingJob.read(folder: recordingFolder)
            check(finished?.phase == nil && finished?.language == .english && finished?.expectedSpeakers == 3,
                  "Beenden: Auftrag wartet, Sprache/Sprecher vom Beenden")
            check(!FileManager.default.fileExists(atPath: recordingFolder.appending(path: MeetingJob.lockFileName).path), "Sperrdatei entfernt")
            check(try WAVFile.inspect(at: recording.audioURL).isConsistent, "WAV nach regulärem Beenden vollständig")

            // Verwerfen einer laufenden Aufnahme räumt auf.
            let cancelled = try queue.beginRecording(recordedAt: Date(), language: .german, expectedSpeakers: nil)
            queue.discardRecording(cancelled)
            check(!FileManager.default.fileExists(atPath: cancelled.audioURL.deletingLastPathComponent().path), "Verwerfen entfernt den Entwurf")

            for item in queue.items { queue.discard(item.id) }
        } catch {
            check(false, "Unerwarteter Fehler: \(error.localizedDescription)")
        }
        return failures
    }

    // MARK: Hilfen

    private static func makeDraft(in queue: URL) throws -> MeetingJob {
        let job = MeetingJob(recordedAt: Date().addingTimeInterval(-600), durationSeconds: 0, language: .german,
                             expectedSpeakers: 2, phase: .recording)
        try FileManager.default.createDirectory(at: MeetingJob.folder(for: job.id, in: queue), withIntermediateDirectories: true)
        try job.write(in: queue)
        return job
    }

    /// Deterministisches Testsignal, damit sich der Inhalt nach der Reparatur vergleichen lässt.
    private static func signal(frames: Int, offset: Int = 0) -> [Float] {
        (0..<frames).map { Float(sin(Double($0 + offset) * 0.031) * 0.4) }
    }

    private static func appendBytes(_ bytes: [UInt8], to url: URL) throws {
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(bytes))
        try handle.close()
    }

    /// So hat `AudioRecorder` bis 1.1.0 geschrieben.
    private static func writeWithAVAudioFile(_ url: URL, frames: Int) throws {
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: Double(rate), AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ]
        let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(frames))!
        buffer.frameLength = AVAudioFrameCount(frames)
        let samples = signal(frames: frames)
        samples.withUnsafeBufferPointer { buffer.floatChannelData![0].update(from: $0.baseAddress!, count: frames) }
        try file.write(from: buffer)
    }

    /// Wie nach einem Absturz: RIFF- und data-Größe 0.
    private static func zeroSizes(_ url: URL) throws {
        let info = try WAVFile.inspect(at: url)
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        for offset in [4, info.dataOffset - 4] {
            try handle.seek(toOffset: UInt64(offset))
            try handle.write(contentsOf: Data(count: 4))
        }
    }

    /// Liest die Datei mit AVFoundation (wie Diarizer und Transkription) und vergleicht mit dem Testsignal.
    private static func verifyReadable(_ url: URL, frames: Int) throws -> Bool {
        let file = try AVAudioFile(forReading: url)
        guard file.length == AVAudioFramePosition(frames), Int(file.processingFormat.sampleRate) == rate,
              let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(frames)) else {
            print("       gelesen: \(file.length) Frames bei \(file.processingFormat.sampleRate) Hz, erwartet \(frames)")
            return false
        }
        // `read(into:)` liefert nicht zwingend alles auf einmal – in Stücken bis zum Dateiende lesen.
        let expected = signal(frames: frames)
        var position = 0
        var maxDiff: Float = 0
        while file.framePosition < file.length {
            try file.read(into: buffer)
            let chunk = UnsafeBufferPointer(start: buffer.floatChannelData![0], count: Int(buffer.frameLength))
            guard !chunk.isEmpty, position + chunk.count <= frames else { break }
            for (i, value) in chunk.enumerated() { maxDiff = max(maxDiff, abs(value - expected[position + i])) }
            position += chunk.count
        }
        if position != frames || maxDiff >= 1e-6 {
            print("       gelesen: \(position) Frames, größte Abweichung \(maxDiff), Format \(file.fileFormat)")
            return false
        }
        return true
    }
}
