import Foundation
import Testing
@testable import LunaCore

@Suite("Aufnahmen retten")
struct RecordingRecoveryTests {
    private static let rate = 16_000

    /// Schreibt `seconds` Sekunde(n) Sinus über den echten Writer und schließt die Datei.
    private func writeWAV(_ url: URL, seconds: Double) throws {
        let writer = try WAVWriter(url: url, sampleRate: Self.rate)
        let samples = (0..<Int(seconds * Double(Self.rate))).map { Float(sin(Double($0) * 0.05) * 0.3) }
        writer.append(samples)
        writer.close()
    }

    /// Simuliert einen Absturz: Größen im Kopf auf `declared` setzen, optional ein halbes Frame anhängen.
    private func breakHeader(_ url: URL, declared: UInt32, partialFrame: Bool) throws {
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        try handle.seek(toOffset: 4)
        try handle.write(contentsOf: Data(le: declared))
        try handle.seek(toOffset: 40)
        try handle.write(contentsOf: Data(le: declared))
        if partialFrame {
            try handle.seekToEnd()
            try handle.write(contentsOf: Data([1, 2, 3]))
        }
    }

    private func draft(in queue: URL, seconds: Double?, phase: MeetingJob.Phase? = .recording) throws -> MeetingJob {
        let job = MeetingJob(recordedAt: Date(timeIntervalSince1970: 1_790_000_000), durationSeconds: 0, language: .german,
                             expectedSpeakers: 3, phase: phase)
        let folder = MeetingJob.folder(for: job.id, in: queue)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try job.write(in: queue)
        if let seconds { try writeWAV(folder.appending(path: MeetingJob.audioFileName), seconds: seconds) }
        return job
    }

    @Test("Writer schreibt einen gültigen Kopf, auch zwischendurch (flush)")
    func writerHeader() throws {
        let url = try Support.temporaryFolder().appending(path: "a.wav")
        let writer = try WAVWriter(url: url, sampleRate: Self.rate, flushInterval: 3600)
        writer.append([Float](repeating: 0.1, count: 8_000))
        writer.flush()
        let mid = try WAVFile.inspect(at: url)
        #expect(mid.isConsistent && mid.frames == 8_000)
        writer.append([Float](repeating: 0.1, count: 8_000))
        writer.close()
        let info = try WAVFile.inspect(at: url)
        #expect(info.isConsistent)
        #expect(info.frames == 16_000 && info.durationSeconds == 1)
        #expect(info.sampleRate == Self.rate && info.channels == 1 && info.dataOffset == WAVFile.canonicalHeaderSize)
        #expect(writer.writeError == nil)
    }

    @Test("Repariert Kopf mit Größe 0 und schneidet ein angefangenes Frame ab")
    func repairsZeroHeader() throws {
        let url = try Support.temporaryFolder().appending(path: "b.wav")
        try writeWAV(url, seconds: 2.5)
        try breakHeader(url, declared: 0, partialFrame: true)
        #expect(try !WAVFile.inspect(at: url).isConsistent)

        let info = try WAVFile.repair(at: url)
        #expect(info.frames == 40_000)
        let reread = try WAVFile.inspect(at: url)
        #expect(reread.isConsistent && reread.durationSeconds == 2.5)
        let size = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int
        #expect(size == WAVFile.canonicalHeaderSize + 40_000 * 4)
    }

    @Test("Repariert einen veralteten Kopf (letzter Flush vor dem Absturz)")
    func repairsStaleHeader() throws {
        let url = try Support.temporaryFolder().appending(path: "c.wav")
        try writeWAV(url, seconds: 3)
        try breakHeader(url, declared: 16_000 * 4, partialFrame: false)
        #expect(try WAVFile.repair(at: url).durationSeconds == 3)
    }

    @Test("Liest Apple-Köpfe mit JUNK-/FLLR-Abschnitten")
    func parsesAppleLayout() throws {
        var head = Data()
        head.append(ascii: "RIFF"); head.append(le: UInt32(0)); head.append(ascii: "WAVE")
        head.append(ascii: "JUNK"); head.append(le: UInt32(28)); head.append(Data(count: 28))
        head.append(ascii: "fmt "); head.append(le: UInt32(16)); head.append(le: UInt16(3)); head.append(le: UInt16(1))
        head.append(le: UInt32(16_000)); head.append(le: UInt32(64_000)); head.append(le: UInt16(4)); head.append(le: UInt16(32))
        head.append(ascii: "FLLR"); head.append(le: UInt32(6)); head.append(Data(count: 6))
        head.append(ascii: "data"); head.append(le: UInt32(0))
        let info = try WAVFile.parse(head, fileSize: head.count + 64_000)
        #expect(info.dataOffset == head.count && info.frames == 16_000 && !info.isConsistent)
        #expect(throws: WAVFile.Error.notWAV) { try WAVFile.parse(Data("hallo welt, keine wav".utf8), fileSize: 21) }
    }

    @Test("Verwaister Entwurf wird gerettet, nicht verarbeitet und nicht gelöscht")
    func recoversOrphanedDraft() throws {
        let queue = try Support.temporaryFolder()
        let job = try draft(in: queue, seconds: 4)
        let folder = MeetingJob.folder(for: job.id, in: queue)
        try breakHeader(folder.appending(path: MeetingJob.audioFileName), declared: 0, partialFrame: true)
        FileManager.default.createFile(atPath: folder.appending(path: MeetingJob.lockFileName).path, contents: nil)

        let report = RecordingRecovery.run(queueFolder: queue)
        #expect(report.recovered.count == 1 && report.discardedEmpty == 0 && report.skippedActive == 0)
        let stored = try MeetingJob.read(folder: folder)
        #expect(stored.phase == .interrupted)
        #expect(stored.durationSeconds == 4)
        #expect(stored.expectedSpeakers == 3 && stored.language == .german)
        #expect(!FileManager.default.fileExists(atPath: folder.appending(path: MeetingJob.lockFileName).path))
        #expect(try WAVFile.inspect(at: folder.appending(path: MeetingJob.audioFileName)).isConsistent)
        // Zweiter Lauf ändert nichts mehr.
        #expect(RecordingRecovery.run(queueFolder: queue).recovered.isEmpty)
        #expect(MeetingJob.readAll(in: queue).map(\.phase) == [.interrupted])
    }

    @Test("Laufende Aufnahme (Sperre gehalten) bleibt unberührt")
    func skipsActiveRecording() throws {
        let queue = try Support.temporaryFolder()
        let job = try draft(in: queue, seconds: 2)
        let folder = MeetingJob.folder(for: job.id, in: queue)
        let lock = try #require(RecordingLock.acquire(at: folder.appending(path: MeetingJob.lockFileName)))
        #expect(job.isRecordingActive(in: queue))
        #expect(RecordingLock.acquire(at: lock.url) == nil)

        let report = RecordingRecovery.run(queueFolder: queue)
        #expect(report.skippedActive == 1 && report.recovered.isEmpty)
        #expect(try MeetingJob.read(folder: folder).phase == .recording)

        lock.release()
        #expect(!job.isRecordingActive(in: queue))
        #expect(RecordingRecovery.run(queueFolder: queue).recovered.count == 1)
    }

    @Test("Leere und sehr kurze Entwürfe werden still verworfen, beendete Aufträge nicht angefasst")
    func discardsEmptyDrafts() throws {
        let queue = try Support.temporaryFolder()
        let short = try draft(in: queue, seconds: 0.4)
        let noAudio = try draft(in: queue, seconds: nil)
        let headerOnly = try draft(in: queue, seconds: nil)
        try WAVFile.header(sampleRate: Self.rate, dataBytes: 0)
            .write(to: MeetingJob.folder(for: headerOnly.id, in: queue).appending(path: MeetingJob.audioFileName))
        let waiting = try draft(in: queue, seconds: 0.4, phase: nil)

        let report = RecordingRecovery.run(queueFolder: queue)
        #expect(report.discardedEmpty == 3 && report.recovered.isEmpty)
        for job in [short, noAudio, headerOnly] {
            #expect(!FileManager.default.fileExists(atPath: MeetingJob.folder(for: job.id, in: queue).path))
        }
        #expect(MeetingJob.readAll(in: queue).map(\.id) == [waiting.id])
    }

    @Test("Lose WAVs der alten Ablage werden als gerettete Aufträge übernommen")
    func importsLegacyRecordings() throws {
        let queue = try Support.temporaryFolder()
        let legacy = try Support.temporaryFolder()
        let file = legacy.appending(path: "\(UUID().uuidString).wav")
        try writeWAV(file, seconds: 2)
        try breakHeader(file, declared: 0, partialFrame: false)
        try writeWAV(legacy.appending(path: "kurz.wav"), seconds: 0.2)
        // Frisch geänderte Dateien gelten als noch in Arbeit (alte Version läuft womöglich parallel).
        #expect(RecordingRecovery.run(queueFolder: queue, legacyFolder: legacy).skippedActive == 2)
        let past = Date().addingTimeInterval(-RecordingRecovery.legacyQuietPeriod - 5)
        for name in try FileManager.default.contentsOfDirectory(atPath: legacy.path) {
            try FileManager.default.setAttributes([.modificationDate: past], ofItemAtPath: legacy.appending(path: name).path)
        }

        let report = RecordingRecovery.run(queueFolder: queue, legacyFolder: legacy, legacyLanguage: .english)
        #expect(report.recovered.count == 1 && report.discardedEmpty == 1)
        let job = try #require(MeetingJob.readAll(in: queue).first)
        #expect(job.phase == .interrupted && job.language == .english && job.durationSeconds == 2)
        #expect(try FileManager.default.contentsOfDirectory(atPath: legacy.path).isEmpty)
    }

    @Test("job.json ohne neue Felder (bis 1.1.0) liest sich als wartender Auftrag")
    func decodesLegacyJob() throws {
        let json = """
        {"durationSeconds":612.5,"expectedSpeakers":2,"id":"6B1E2B0A-6E0E-4C59-8C55-2D8C1D8E2F11",
         "language":"de","recordedAt":"2026-09-30T08:15:00Z"}
        """
        let job = try JSONCoding.decoder().decode(MeetingJob.self, from: Data(json.utf8))
        #expect(job.phase == nil && job.failure == nil && job.durationSeconds == 612.5 && job.expectedSpeakers == 2)
        let encoded = String(decoding: try JSONCoding.encoder().encode(job), as: UTF8.self)
        #expect(!encoded.contains("phase"))

        var draft = job
        draft.phase = .recording
        let roundTrip = try JSONCoding.decoder().decode(MeetingJob.self, from: try JSONCoding.encoder().encode(draft))
        #expect(roundTrip == draft)
    }
}
