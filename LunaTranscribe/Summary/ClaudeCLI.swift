import Foundation
import LunaCore

/// `claude -p` als reine Textverarbeitung: ohne Werkzeuge, ohne MCP-Server, ohne Projekt- oder
/// Nutzereinstellungen, ohne gespeicherte Sitzung. Text per stdin, Antwort über stdout.
/// Wird auch von `ClaudeRefiner` (Nachbearbeitung mit Haiku) benutzt.
struct ClaudeCLI: SummaryCLI {
    let process: CLIProcess

    static func locate(customPath: String) -> ClaudeCLI? {
        CLIProcess.locate(.claude, customPath: customPath).map(ClaudeCLI.init(process:))
    }

    func complete(instructions: String, input: String, model: String?, timeout: TimeInterval) async throws -> String {
        try await complete(instructions: instructions, input: input, model: model, effort: nil, timeout: timeout)
    }

    /// - Parameter effort: Aufwandsstufe der CLI (`low` … `max`); `nil` = Standard.
    func complete(instructions: String, input: String, model: String?, effort: String?, timeout: TimeInterval = 300) async throws -> String {
        var arguments = [
            "-p",
            "--tools", "",
            "--strict-mcp-config",
            "--setting-sources", "",
            "--no-session-persistence",
            "--disable-slash-commands",
            "--output-format", "text",
            "--system-prompt", instructions,
        ]
        if let model, !model.isEmpty { arguments += ["--model", model] }
        if let effort { arguments += ["--effort", effort] }
        let output = try await process.run(arguments: arguments, input: input, timeout: timeout)
        guard !output.isEmpty else { throw CLIError.emptyOutput(.claude) }
        return output
    }
}
