import Foundation
import FoundationModels
import os

private let log = Logger(subsystem: "com.maximhofmeister.LunaTranscribe", category: "refine")

/// Nachbearbeitung mit Apples On-Device-Systemmodell (Foundation Models, macOS 26+).
/// Kein Download, läuft auf der Neural Engine – setzt aber aktiviertes Apple Intelligence voraus.
/// Das Framework wird nur schwach gelinkt (`-weak_framework FoundationModels`), damit die App
/// weiterhin auf macOS 15 startet; `RefinerManager` legt diese Klasse nur unter `#available` an.
@available(macOS 26, *)
final class AppleIntelligenceRefiner: TextRefiner {
    let kind: RefinerKind = .appleIntelligence

    private(set) var isLoaded = false

    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if isLoaded { return }
        if case .unavailable(let reason) = kind.availability {
            throw RefinerError.unavailable(reason)
        }
        progress(0.5)
        // Lädt das Modell in den Speicher; die eigentlichen Sessions sind pro Diktat neu.
        LanguageModelSession(instructions: RefinePrompt.instructions(language: .german, template: RefinePrompt.defaultTemplate)).prewarm()
        isLoaded = true
        progress(1.0)
        log.notice("Apple Intelligence ready")
    }

    func refine(_ text: String, language: AppLanguage, promptTemplate: String) async throws -> String {
        guard isLoaded else { throw RefinerError.notLoaded }

        // Neue Session pro Aufruf: zustandslos, kein wachsendes Kontextfenster. Die Beispielpaare
        // wandern als Text in die Anweisung – Apples Modell ist groß genug, das nicht nachzuahmen.
        var system = RefinePrompt.instructions(language: language, template: promptTemplate)
        for example in RefinePrompt.examples(for: language) {
            system += "\nExample dictation: \(example.input)\nExample result: \(example.output)"
        }
        let session = LanguageModelSession(instructions: system)
        let raw: String
        do {
            let response = try await session.respond(to: RefinePrompt.userMessage(for: text), options: GenerationOptions(sampling: .greedy))
            raw = response.content
        } catch let error as LanguageModelSession.GenerationError {
            throw RefinerError.unavailable(Self.describe(error))
        }

        guard let cleaned = RefinePrompt.sanitize(raw, original: text) else {
            log.error("implausible output for \(text.count) chars: \(raw.prefix(120), privacy: .private)")
            throw RefinerError.implausibleOutput
        }
        return cleaned
    }

    func unload() {
        isLoaded = false
    }

    private static func describe(_ error: LanguageModelSession.GenerationError) -> String {
        switch error {
        case .guardrailViolation, .refusal:
            return "Apple Intelligence hat den Text abgelehnt (Inhaltsfilter)."
        case .exceededContextWindowSize:
            return "Das Diktat ist zu lang für Apple Intelligence."
        case .unsupportedLanguageOrLocale:
            return "Apple Intelligence unterstützt diese Sprache nicht."
        case .assetsUnavailable:
            return "Das Systemmodell ist nicht verfügbar."
        default:
            return error.localizedDescription
        }
    }
}
