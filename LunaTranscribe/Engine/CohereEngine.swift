import Foundation
import CoreML
import FluidAudio
import Synchronization

/// Cohere Transcribe (2B) via CoreML – INT8-Encoder + ANE-Decoder aus FluidAudio.
final class CohereEngine: TranscriptionEngine {
    let kind: EngineKind = .cohere

    private var models: CoherePipeline.LoadedModels?
    private let pipeline = CoherePipeline()
    /// Wird vom Main Actor gesetzt und (im Meeting) von Hintergrund-Tasks gelesen.
    private let vocabulary = Mutex(CustomVocabulary.disabled)

    var isLoaded: Bool { models != nil }

    /// FluidAudio legt die Dateien unter <base>/<repo.folderName> ab, also .../cohere-transcribe/q8
    private var modelDir: URL {
        ModelPaths.base.appendingPathComponent(Repo.cohereTranscribeCoreml.folderName, isDirectory: true)
    }

    private var filesPresent: Bool {
        let fm = FileManager.default
        return ModelNames.CohereTranscribe.requiredModels.allSatisfy {
            fm.fileExists(atPath: modelDir.appendingPathComponent($0).path)
        }
    }

    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if models != nil { return }

        if !filesPresent {
            // Einzige Netzwerkverbindung der App: einmaliger Modell-Download von Hugging Face.
            try await ModelHub.download(.cohereTranscribeCoreml, to: ModelPaths.base) { p in
                progress(p.fractionCompleted * 0.9)
            }
        }
        progress(0.9)

        models = try await CoherePipeline.loadModels(
            encoderDir: modelDir,
            decoderDir: modelDir,
            vocabDir: modelDir,
            decoderVariant: .v2,
            // Wichtig: NICHT .all – damit landet der Encoder auf der GPU (MPSGraph) und braucht
            // bei jedem Start ~90 s Kompilierung ohne Cache. CPU+ANE kompiliert einmalig (~40 s)
            // und wird danach systemweit gecacht → Diktate in Sekundenbruchteilen.
            computeUnits: .cpuAndNeuralEngine
        )
        progress(0.95)

        // Warm-up: erste Inferenz löst die (einmalige) ANE-Kompilierung aus.
        // Lieber jetzt beim Start als beim ersten Diktat.
        let silence = [Float](repeating: 0, count: CohereAsrConfig.sampleRate)
        _ = try? await pipeline.transcribe(audio: silence, models: models!, language: .english)
        progress(1.0)
    }

    func transcribe(_ samples: [Float], language: AppLanguage) async throws -> String {
        guard let models else { throw EngineError.notLoaded }
        guard let lang = language.cohere else { throw EngineError.unsupportedLanguage(language, kind) }

        // transcribeLong reicht kurze Aufnahmen (≤ 35 s) direkt durch und
        // schneidet längere in überlappende Fenster.
        let result = try await pipeline.transcribeLong(audio: samples, models: models, language: lang)
        let text = result.text.trimmingCharacters(in: .whitespacesAndNewlines)
        return vocabulary.withLock { $0 }.correct(text)
    }

    /// Cohere hat keinen CTC-Kopf und liefert keine Zeitstempel – nur die Schreibweisen-Korrektur.
    func configureVocabulary(_ vocabulary: CustomVocabulary) async throws -> Bool {
        self.vocabulary.withLock { $0 = vocabulary }
        return false
    }

    func unload() {
        models = nil
    }
}
