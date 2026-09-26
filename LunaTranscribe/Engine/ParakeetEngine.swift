import Foundation
import FluidAudio
import LunaCore
import Synchronization
import os

/// NVIDIA Parakeet TDT 0.6B v3 via CoreML (FluidAudio). Klein und sehr schnell, 25 Sprachen.
///
/// Eigenes Wörterbuch in zwei Stufen: FluidAudios CTC-Vocabulary-Boosting prüft mit einem zweiten,
/// kleinen Modell (Parakeet CTC 110M), ob das Audio eher nach einem Wörterbuch-Begriff klingt als nach
/// dem erkannten Wort, und ersetzt es dann; danach korrigiert `CustomVocabulary` bekannte Schreibweisen.
final class ParakeetEngine: TranscriptionEngine {
    let kind: EngineKind = .parakeet

    private var manager: AsrManager?

    /// Beide werden vom Main Actor gesetzt und (im Meeting) von Hintergrund-Tasks gelesen.
    private let vocabulary = Mutex(CustomVocabulary.disabled)
    private let boosting = Mutex<VocabularyBoostingSession?>(nil)
    /// CTC-Modell fürs Boosting; einmal geladen, bleibt es bis `unload()` im Speicher.
    private var ctcModels: CtcModels?

    var isLoaded: Bool { manager != nil }

    private var modelDir: URL {
        AppPaths.models.appendingPathComponent(Repo.parakeetV3.folderName, isDirectory: true)
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
        var text = result.text

        if let session = boosting.withLock({ $0 }), let timings = result.tokenTimings,
           // `rescore` fängt eigene Fehler ab und liefert dann nil – der Rohtext bleibt.
           let output = await session.rescore(text: text, tokenTimings: timings, audioSamples: samples),
           output.wasModified {
            text = Self.applyBoostingReplacements(output.replacements, to: text)
        }

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return vocabulary.withLock { $0 }.correct(trimmed)
    }

    func configureVocabulary(_ vocabulary: CustomVocabulary) async throws -> Bool {
        // Schreibweisen-Korrektur sofort; Boosting erst, wenn die Session steht.
        self.vocabulary.withLock { $0 = vocabulary }
        guard !vocabulary.isEmpty else {
            boosting.withLock { $0 = nil }
            return false
        }

        // Einmaliger Download (~100 MB). FluidAudio sucht den Tokenizer des CTC-Modells fest in
        // seinem eigenen Cache-Ordner, deshalb liegt das Modell nicht unter `AppPaths.models`.
        let models: CtcModels
        if let ctcModels {
            models = ctcModels
        } else {
            models = try await CtcModels.downloadAndLoad(variant: .ctc110m)
            ctcModels = models
        }

        let context = CustomVocabularyContext(terms: vocabulary.entries.map { entry in
            CustomVocabularyTerm(text: entry.term, aliases: entry.aliases.isEmpty ? nil : entry.aliases)
        })
        let session = try await VocabularyBoostingSession(vocabulary: context, ctcModels: models, config: Self.rescorerConfig)
        boosting.withLock { $0 = session }
        Log.vocabulary.notice("Boosting aktiv mit \(context.terms.count) Begriffen")
        return true
    }

    /// Ohne den „Spotter-Rescue“ von FluidAudio: Der schlägt Begriffe allein aufgrund des
    /// (englischen) CTC-Modells vor, auch wenn das erkannte Wort ganz anders aussieht. Bei deutschen
    /// Diktaten wurde so aus „Das Wetter ist heute schön“ „weclapp weclapp“ und aus „Maximilian“ „Grafana“.
    /// Ohne Rescue blieben im Selbsttest alle echten Treffer erhalten und alle Fehltreffer verschwanden.
    private static let rescorerConfig = VocabularyRescorer.Config(spotterRescueEnabled: false)

    /// Übernimmt nur die Entscheidungen des Rescorers („Luna Transkribe“ → „LunaTranscribe“), nicht seinen
    /// Text: Den baut FluidAudio aus Wort-Zeitstempeln neu und verliert dabei Satzzeichen am ersetzten Wort.
    /// Die Ersetzung selbst läuft über dieselbe Wortgrenzen-Logik wie das Wörterbuch.
    private static func applyBoostingReplacements(_ replacements: [VocabularyRescorer.RescoringResult], to text: String) -> String {
        let entries: [VocabularyEntry] = replacements.compactMap { replacement in
            guard replacement.shouldReplace, let term = replacement.replacementWord else { return nil }
            let original = replacement.originalWord.trimmingCharacters(in: .punctuationCharacters.union(.whitespaces))
            Log.vocabulary.debug("Boosting: \(original) → \(term)")
            return VocabularyEntry(term: term, aliases: [original])
        }
        return CustomVocabulary(entries: entries).correct(text)
    }

    func unload() {
        manager = nil
        boosting.withLock { $0 = nil }
        ctcModels = nil
    }
}
