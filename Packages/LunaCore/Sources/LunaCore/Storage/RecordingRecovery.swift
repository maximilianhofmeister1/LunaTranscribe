import Foundation

/// Rettet Meeting-Aufnahmen, die beim letzten Lauf nicht regulär beendet wurden (Absturz, `kill`,
/// Herunterfahren mit laufendem Meeting). Läuft beim Start der App, bevor die Warteschlange ihre
/// Aufträge einliest.
///
/// Ein Auftrag in Phase `recording`, dessen `RecordingLock` niemand mehr hält, ist verwaist: Die WAV
/// wird repariert, die Dauer aus der Dateigröße bestimmt und der Auftrag als `interrupted` markiert –
/// verarbeitet wird erst auf Klick des Nutzers. Gelöscht werden nur Aufträge ohne nennenswertes Audio.
/// Hält jemand die Sperre (zweite Instanz, Selbsttest per `open -n`), bleibt der Auftrag unberührt.
public enum RecordingRecovery {
    public struct Report: Equatable, Sendable {
        /// Gerettete Aufträge (jetzt `interrupted`).
        public var recovered: [MeetingJob] = []
        /// Ohne nennenswertes Audio still entfernt.
        public var discardedEmpty = 0
        /// Aufnahme läuft noch in einem anderen Prozess.
        public var skippedActive = 0
        /// Kopf nicht reparierbar; Auftrag bleibt trotzdem erhalten (Verarbeitung meldet dann einen Fehler).
        public var unrepairable: [String] = []

        public init() {}
    }

    /// Kürzere Entwürfe enthalten nichts Verwertbares und werden still verworfen.
    public static let minimumDuration: TimeInterval = 1
    /// Lose WAVs der alten Ablage, die so kürzlich geändert wurden, gelten als noch in Arbeit – die alte
    /// Version (ohne Sperre) könnte parallel laufen und gerade hineinschreiben.
    public static let legacyQuietPeriod: TimeInterval = 60

    /// - Parameters:
    ///   - legacyFolder: Ordner der alten Aufnahmen-Ablage (bis 1.1.0: `AppPaths.meetingRecordings`, lose
    ///     WAVs ohne Auftrag). Dort liegende Reste werden repariert und als gerettete Aufträge übernommen.
    ///   - legacyLanguage: Sprache für diese Altlasten (sie haben keine Metadaten).
    public static func run(queueFolder: URL, legacyFolder: URL? = nil, legacyLanguage: AppLanguage = .german) -> Report {
        var report = Report()
        let fm = FileManager.default
        let folders = (try? fm.contentsOfDirectory(at: queueFolder, includingPropertiesForKeys: nil)) ?? []
        for folder in folders {
            guard var job = try? MeetingJob.read(folder: folder) else { continue }
            let lockURL = folder.appending(path: MeetingJob.lockFileName)
            guard job.phase == .recording else {
                // Übrig gebliebene Sperrdatei eines regulär beendeten Auftrags (Absturz zwischen zwei Schritten).
                if fm.fileExists(atPath: lockURL.path), !RecordingLock.isHeld(at: lockURL) { try? fm.removeItem(at: lockURL) }
                continue
            }
            guard let lock = RecordingLock.acquire(at: lockURL) else {
                report.skippedActive += 1
                continue
            }
            defer { lock.release() }

            let audio = folder.appending(path: MeetingJob.audioFileName)
            let size = (try? fm.attributesOfItem(atPath: audio.path)[.size] as? Int) ?? 0
            do {
                let info = try WAVFile.repair(at: audio)
                guard info.durationSeconds >= minimumDuration else {
                    try? fm.removeItem(at: folder)
                    report.discardedEmpty += 1
                    continue
                }
                job.durationSeconds = info.durationSeconds
            } catch {
                // Kein Audio jenseits des Kopfes: nichts zu retten. Sonst behalten – lieber ein Auftrag,
                // der beim Verarbeiten scheitert, als eine gelöschte Stunde Meeting.
                guard size > WAVFile.canonicalHeaderSize else {
                    try? fm.removeItem(at: folder)
                    report.discardedEmpty += 1
                    continue
                }
                job.durationSeconds = estimatedDuration(bytes: size)
                report.unrepairable.append(folder.lastPathComponent)
            }
            job.phase = .interrupted
            do {
                try job.write(in: queueFolder)
                lock.release(removeFile: true)
                report.recovered.append(job)
            } catch {
                // Bleibt `recording` und wird beim nächsten Start erneut versucht.
                report.unrepairable.append(folder.lastPathComponent)
            }
        }
        if let legacyFolder { importLegacy(from: legacyFolder, into: queueFolder, language: legacyLanguage, report: &report) }
        report.recovered.sort { $0.recordedAt < $1.recordedAt }
        return report
    }

    /// Lose WAVs der alten Ablage als gerettete Aufträge übernehmen. Unlesbare bleiben liegen.
    private static func importLegacy(from folder: URL, into queueFolder: URL, language: AppLanguage, report: inout Report) {
        let fm = FileManager.default
        let keys: [URLResourceKey] = [.creationDateKey, .contentModificationDateKey]
        guard let files = try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: keys) else { return }
        for file in files where file.pathExtension.lowercased() == "wav" {
            let values = try? file.resourceValues(forKeys: Set(keys))
            if let modified = values?.contentModificationDate, Date().timeIntervalSince(modified) < legacyQuietPeriod {
                report.skippedActive += 1
                continue
            }
            guard let info = try? WAVFile.repair(at: file) else {
                report.unrepairable.append(file.lastPathComponent)
                continue
            }
            guard info.durationSeconds >= minimumDuration else {
                try? fm.removeItem(at: file)
                report.discardedEmpty += 1
                continue
            }
            let recordedAt = values?.creationDate
                ?? values?.contentModificationDate?.addingTimeInterval(-info.durationSeconds) ?? Date()
            let job = MeetingJob(recordedAt: recordedAt, durationSeconds: info.durationSeconds, language: language,
                                 expectedSpeakers: nil, phase: .interrupted)
            let target = MeetingJob.folder(for: job.id, in: queueFolder)
            do {
                // Erst der Auftrag, dann die Datei: Scheitert das Verschieben, bleibt sie in der alten Ablage.
                try fm.createDirectory(at: target, withIntermediateDirectories: true)
                try job.write(in: queueFolder)
                try fm.moveItem(at: file, to: target.appending(path: MeetingJob.audioFileName))
                report.recovered.append(job)
            } catch {
                try? fm.removeItem(at: target)
                report.unrepairable.append(file.lastPathComponent)
            }
        }
    }

    /// Dauer, falls der Kopf unlesbar ist: Annahme 16 kHz mono Float32 (so nimmt die App auf).
    static func estimatedDuration(bytes: Int) -> TimeInterval {
        Double(max(0, bytes - WAVFile.canonicalHeaderSize)) / (16_000 * 4)
    }
}
