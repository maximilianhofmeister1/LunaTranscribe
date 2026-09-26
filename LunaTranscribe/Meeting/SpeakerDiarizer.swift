import Foundation
import FluidAudio
import LunaCore

/// Ein zusammenhängender Abschnitt, in dem ein Sprecher spricht (Sekunden ab Aufnahmestart).
struct SpeakerSegment {
    let speakerID: String
    let start: TimeInterval
    let end: TimeInterval
}

/// Sprecher-Erkennung (Diarization) über FluidAudios Offline-Pipeline: pyannote community-1
/// (Segmentierung + WeSpeaker-Embeddings + VBx-Clustering) als CoreML – komplett lokal.
/// Die Erkennung ist rein akustisch (Stimm-Embeddings), unabhängig davon, ob eine Stimme über das
/// Mikrofon oder das Systemaudio kam. Die Modelle sind klein (< 100 MB) und landen wie die
/// ASR-Modelle unter `AppPaths.models`.
final class SpeakerDiarizer {
    private var models: OfflineDiarizerModels?

    /// Lädt (beim ersten Mal: lädt herunter und kompiliert) die Modelle. `progress` 0…1.
    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if models != nil { return }
        models = try await OfflineDiarizerModels.load(from: AppPaths.models) { p in
            progress(p.fractionCompleted)
        }
        progress(1)
    }

    /// Diarisiert eine Audiodatei (memory-mapped, konstanter Speicher). `progress` 0…1.
    /// - Parameter expectedSpeakers: Bekannte Teilnehmerzahl (nil = automatisch). Das Clustering
    ///   ist konservativ und fasst bei leisen oder ähnlichen Stimmen gern alles zu einem Sprecher
    ///   zusammen – die Vorgabe zwingt es, die Embeddings auf genau diese Zahl aufzuteilen.
    func diarize(_ url: URL, expectedSpeakers: Int?,
                 progress: @escaping @Sendable (Double) -> Void) async throws -> [SpeakerSegment] {
        guard let models else { throw OfflineDiarizationError.modelNotLoaded("Diarizer") }

        // Der Manager trägt die Konfiguration unveränderlich – deshalb pro Lauf ein neuer,
        // die (teuren) Modelle werden wiederverwendet.
        var config = OfflineDiarizerConfig.default
        if let n = expectedSpeakers, n > 0 { config.clustering.numSpeakers = n }
        let manager = OfflineDiarizerManager(config: config)
        manager.initialize(models: models)

        let result = try await manager.process(url) { done, total in
            guard total > 0 else { return }
            progress(Double(done) / Double(total))
        }
        return result.segments
            .map { SpeakerSegment(speakerID: $0.speakerId, start: TimeInterval($0.startTimeSeconds), end: TimeInterval($0.endTimeSeconds)) }
            .sorted { $0.start < $1.start }
    }
}
