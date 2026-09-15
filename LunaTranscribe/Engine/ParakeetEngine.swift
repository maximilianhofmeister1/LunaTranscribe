import Foundation
import FluidAudio

/// NVIDIA Parakeet TDT 0.6B v3 via CoreML (FluidAudio). Klein und sehr schnell, 25 Sprachen.
final class ParakeetEngine: TranscriptionEngine {
    let kind: EngineKind = .parakeet

    private var manager: AsrManager?

    var isLoaded: Bool { manager != nil }

    private var modelDir: URL {
        ModelPaths.base.appendingPathComponent(Repo.parakeetV3.folderName, isDirectory: true)
    }

    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if manager != nil { return }
        let models = try await AsrModels.downloadAndLoad(to: modelDir, version: .v3) { p in
            progress(p.fractionCompleted)
        }
        let manager = AsrManager(config: .default, models: models)
        self.manager = manager
        progress(0.95)

        // Warm-up (siehe CohereEngine)
        var state = TdtDecoderState.make()
        let silence = [Float](repeating: 0, count: 16_000)
        _ = try? await manager.transcribe(silence, decoderState: &state, language: .english)
        progress(1.0)
    }

    func transcribe(_ samples: [Float], language: AppLanguage) async throws -> String {
        guard let manager else { throw EngineError.notLoaded }
        guard let lang = language.parakeet else { throw EngineError.unsupportedLanguage(language, kind) }

        // Frischer Decoder-Zustand pro Diktat – kein Kontext zwischen Aufnahmen.
        var state = TdtDecoderState.make()
        let result = try await manager.transcribe(samples, decoderState: &state, language: lang)
        return result.text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func unload() {
        manager = nil
    }
}
