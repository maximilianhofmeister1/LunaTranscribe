import Foundation
import LunaCore
import os

/// Nachbearbeitung mit Claude Haiku über die lokal installierte Claude CLI (`claude -p`) – mit dem
/// Claude-Konto des Nutzers, ohne API-Schlüssel. Niedriger Aufwand (`--effort low`), weil das Bereinigen
/// eines Diktats kein Nachdenken braucht und jede Sekunde vor dem Einfügen zählt.
///
/// Achtung Local-First: Das Diktat verlässt den Mac und geht an Anthropic. Die Einstellungen weisen
/// darauf hin; Rohtext und Audio bleiben wie immer lokal.
final class ClaudeRefiner: TextRefiner {
    let kind: RefinerKind = .claudeHaiku

    /// Explizite Modell-ID statt des Alias `haiku`: Ältere CLI-Versionen lösen den Alias noch auf
    /// Haiku 4.5 auf, das im Test (2026-11-02) 10–75 s brauchte; Haiku 5.5 antwortet in 0,6–2,3 s.
    /// Kennt die CLI die ID nicht, warnt sie nur auf stderr und reicht sie trotzdem durch.
    static let model = "claude-haiku-5-5"
    static let effort = "low"

    /// Großzügiger als bei MLX: Die CLI startet bei jedem Diktat neu und braucht das Netz.
    private let timeout: TimeInterval = 30

    private var cli: ClaudeCLI?

    var isLoaded: Bool { cli != nil }

    /// Nichts zu laden – nur die CLI finden und prüfen, dass sie startet. Die Anmeldung zeigt sich
    /// erst beim ersten Diktat (eine Prüfung kostete selbst einen Modellaufruf).
    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if cli != nil { return }
        progress(0.3)
        let customPath = await MainActor.run { SettingsStore.shared.claudeCLIPath }
        // `locate` fragt im Zweifel die Login-Shell – nicht auf dem Main Thread.
        guard let found = await Task.detached(operation: { ClaudeCLI.locate(customPath: customPath) }).value else {
            throw RefinerError.unavailable(ClaudeCLI.CLIError.notFound.localizedDescription)
        }
        let version = try await found.version()
        cli = found
        progress(1.0)
        Log.refine.notice("Claude CLI ready: \(version, privacy: .public)")
    }

    func refine(_ text: String, language: AppLanguage, promptTemplate: String) async throws -> String {
        guard let cli else { throw RefinerError.notLoaded }

        // Wie bei Apple Intelligence: Beispielpaare als Text in der Anweisung – Haiku ahmt das Format nicht nach.
        var system = RefinePrompt.instructions(language: language, template: promptTemplate)
        for example in RefinePrompt.examples(for: language) {
            system += "\nExample dictation: \(example.input)\nExample result: \(example.output)"
        }

        let raw: String
        do {
            raw = try await cli.complete(instructions: system, input: RefinePrompt.userMessage(for: text),
                                         model: Self.model, effort: Self.effort, timeout: timeout)
        } catch ClaudeCLI.CLIError.timedOut {
            throw RefinerError.timeout
        } catch ClaudeCLI.CLIError.failed(_, let message)
                    where message.localizedCaseInsensitiveContains("auth") || message.localizedCaseInsensitiveContains("login") {
            // Kurz halten – der Grund erscheint im Overlay.
            throw RefinerError.unavailable("Claude CLI nicht angemeldet (im Terminal „claude“ starten)")
        } catch let error as ClaudeCLI.CLIError {
            throw RefinerError.unavailable(error.localizedDescription)
        }

        guard let cleaned = RefinePrompt.sanitize(raw, original: text) else {
            Log.refine.error("implausible output for \(text.count) chars: \(raw.prefix(120), privacy: .private)")
            throw RefinerError.implausibleOutput
        }
        return cleaned
    }

    func unload() {
        cli = nil
    }
}
