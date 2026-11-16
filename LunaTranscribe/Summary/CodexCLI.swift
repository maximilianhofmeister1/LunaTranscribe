import Foundation
import LunaCore

/// OpenAIs Codex CLI (`codex exec`) mit dem ChatGPT-Konto des Nutzers (auch Free). Die Anmeldung
/// von `codex login` liegt in `~/.codex`; `exec` nutzt sie ohne Rückfrage.
///
/// `codex exec` ist ein Coding-Agent. Damit er nur zusammenfasst: Sandbox nur lesend im leeren Arbeitsordner,
/// keine Freigaben, keine Websuche, keine Nutzer-Konfiguration (also auch keine MCP-Server aus
/// `~/.codex/config.toml`), kein AGENTS.md, keine gespeicherte Sitzung. Die Anweisung geht als
/// `developer_instructions` hinein, das Transkript per stdin als Kontext zum kurzen Prompt.
/// Auf stdout steht nur die letzte Antwort des Agenten, der Fortschritt geht nach stderr.
struct CodexCLI: SummaryCLI {
    let process: CLIProcess

    static let task = "Fasse das mitgelieferte Meeting-Transkript nach den Entwickler-Anweisungen zusammen. Antworte ausschließlich mit der Zusammenfassung."

    func complete(instructions: String, input: String, model: String?, timeout: TimeInterval) async throws -> String {
        var arguments = [
            "exec",
            "--ephemeral",
            "--skip-git-repo-check",
            "--ignore-user-config",
            "--sandbox", "read-only",
            "--color", "never",
            "-c", "approval_policy=\"never\"",
            "-c", "web_search=\"disabled\"",
            "-c", "project_doc_max_bytes=0",
            "-c", "developer_instructions=\(Self.tomlString(instructions))",
        ]
        if let model, !model.isEmpty { arguments += ["--model", model] }
        arguments.append(Self.task)
        let output = try await process.run(arguments: arguments, input: input, timeout: timeout)
        guard !output.isEmpty else { throw CLIError.emptyOutput(.codex) }
        return output
    }

    /// `-c key=value` liest den Wert als TOML: als einfacher String in Anführungszeichen, mit maskierten
    /// Backslashes, Anführungszeichen und Steuerzeichen (Zeilenumbrüche als `\n`).
    static func tomlString(_ text: String) -> String {
        var result = "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": result += "\\\\"
            case "\"": result += "\\\""
            case "\n": result += "\\n"
            case "\r": result += "\\r"
            case "\t": result += "\\t"
            case _ where scalar.value < 0x20 || scalar.value == 0x7F:
                result += String(format: "\\u%04X", scalar.value)
            default: result.unicodeScalars.append(scalar)
            }
        }
        return result + "\""
    }
}
