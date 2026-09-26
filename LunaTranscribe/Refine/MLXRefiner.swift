import Foundation
import Hub
import LunaCore
import MLX
import MLXLLM
import MLXLMCommon
import os

/// Nachbearbeitung mit einem quantisierten Open-Weights-Modell (Qwen3, LFM2) über MLX.
/// Rechnet auf der GPU (Metal, Unified Memory) – nicht auf der Neural Engine, die nur CoreML erreicht.
/// Die Gewichte kommen einmalig von Hugging Face und liegen wie die ASR-Modelle unter `AppPaths.models`.
final class MLXRefiner: TextRefiner {
    let kind: RefinerKind

    /// Harte Obergrenze pro Diktat; danach wird der Rohtext eingefügt, damit nichts hängen bleibt.
    private let timeout: TimeInterval = 20
    /// Obergrenze für die Antwortlänge, damit ein Modell nicht endlos weiterschreibt.
    private let maxOutputTokens = 1024

    private var container: ModelContainer?

    init(kind: RefinerKind) {
        precondition(kind.huggingFaceID != nil, "MLXRefiner braucht ein Hugging-Face-Modell")
        self.kind = kind
    }

    var isLoaded: Bool { container != nil }

    func prepare(progress: @escaping @Sendable (Double) -> Void) async throws {
        if container != nil { return }
        guard let repo = kind.huggingFaceID else { throw RefinerError.unavailable("Kein MLX-Modell.") }

        // Kleiner Metal-Puffer-Cache: Das Modell bleibt dauerhaft geladen, freier Speicher soll
        // aber an andere Apps zurückgehen.
        MLX.GPU.set(cacheLimit: 20 * 1024 * 1024)

        // Modelle landen unter ~/Library/Application Support/LunaTranscribe/Models/models/<repo>
        let hub = HubApi(downloadBase: AppPaths.models)
        let loaded = try await LLMModelFactory.shared.loadContainer(
            hub: hub, configuration: ModelConfiguration(id: repo)
        ) { p in
            progress(p.fractionCompleted * 0.9)
        }
        container = loaded
        progress(0.95)

        // Warm-up: Metal-Kernel kompilieren und Gewichte in den Speicher holen, damit das erste
        // Diktat nicht Sekunden auf die GPU wartet.
        _ = try? await generate(with: loaded, messages: [.system("Repeat the text."), .user("ok")], maxTokens: 4)
        progress(1.0)
        Log.refine.notice("\(self.kind.shortLabel, privacy: .public) loaded")
    }

    func refine(_ text: String, language: AppLanguage, promptTemplate: String) async throws -> String {
        guard let container else { throw RefinerError.notLoaded }
        let chat = RefinePrompt.messages(for: text, language: language, template: promptTemplate).map(Self.chatMessage)

        let raw = try await withTimeout(timeout) { [self] in
            try await generate(with: container, messages: chat, maxTokens: outputBudget(for: text))
        }
        guard let cleaned = RefinePrompt.sanitize(raw, original: text) else {
            Log.refine.error("implausible output for \(text.count) chars: \(raw.prefix(120), privacy: .private)")
            throw RefinerError.implausibleOutput
        }
        return cleaned
    }

    func unload() {
        container = nil
        MLX.GPU.clearCache()
    }

    // MARK: Intern

    /// Erlaubt bis zu doppelt so viele Tokens wie die Eingabe (Interpunktion, Großschreibung)
    /// plus etwas Reserve; grob 1 Token ≈ 3 Zeichen für deutsche Texte.
    private func outputBudget(for text: String) -> Int {
        min(maxOutputTokens, text.count * 2 / 3 + 64)
    }

    private static func chatMessage(_ message: RefinePrompt.Message) -> Chat.Message {
        switch message.role {
        case .system: return .system(message.content)
        case .user: return .user(message.content)
        case .assistant: return .assistant(message.content)
        }
    }

    /// Ein einzelner, zustandsloser Chat-Durchlauf. Greedy, ohne Thinking.
    private func generate(with container: ModelContainer, messages: [Chat.Message], maxTokens: Int) async throws -> String {
        let input = UserInput(
            chat: messages,
            // Qwen3: Reasoning-Block abschalten; andere Modelle ignorieren den Schlüssel.
            additionalContext: ["enable_thinking": false]
        )
        let parameters = GenerateParameters(maxTokens: maxTokens, temperature: 0)

        return try await container.perform { context in
            let lmInput = try await context.processor.prepare(input: input)
            let stream = try MLXLMCommon.generate(input: lmInput, parameters: parameters, context: context)
            var output = ""
            for await generation in stream {
                try Task.checkCancellation()
                switch generation {
                case .chunk(let chunk):
                    output += chunk
                case .info(let info):
                    Log.refine.notice("generated \(info.generationTokenCount) tokens at \(info.tokensPerSecond, format: .fixed(precision: 0)) tok/s (prompt \(info.promptTokenCount) tokens)")
                case .toolCall:
                    break
                }
            }
            return output
        }
    }

    /// Bricht `operation` nach `seconds` ab. Die Generierung selbst läuft auf dem Modell-Actor weiter,
    /// bis sie `Task.checkCancellation()` sieht – der Aufrufer wartet aber nicht länger.
    private func withTimeout<T: Sendable>(_ seconds: TimeInterval, _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await operation() }
            group.addTask {
                try await Task.sleep(for: .seconds(seconds))
                throw RefinerError.timeout
            }
            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }
}
