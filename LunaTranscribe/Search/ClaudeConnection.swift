import AppKit
import Foundation

/// Einrichtung von luna-mcp in Claude: fertige Befehle zum Kopieren und ein Verbindungstest.
/// Die App schreibt bewusst nicht selbst in die Konfiguration anderer Programme.
enum ClaudeConnection {
    /// luna-mcp liegt neben der App-Binärdatei in Contents/MacOS.
    static var helperURL: URL {
        (Bundle.main.executableURL ?? Bundle.main.bundleURL.appending(path: "Contents/MacOS/LunaTranscribe"))
            .deletingLastPathComponent()
            .appending(path: "luna-mcp")
    }

    /// Befehl für Claude Code (für alle Projekte des Nutzers).
    static var claudeCodeCommand: String {
        "claude mcp add --scope user --transport stdio lunatranscribe -- '\(helperURL.path)'"
    }

    /// Eintrag für `~/Library/Application Support/Claude/claude_desktop_config.json`.
    static var claudeDesktopConfig: String {
        """
        {
          "mcpServers": {
            "lunatranscribe": {
              "command": "\(helperURL.path)"
            }
          }
        }
        """
    }

    static func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    /// Startet `luna-mcp --check` (nur lesend) und liefert dessen Ausgabe.
    static func check() async -> Result<String, Error> {
        await Task.detached(priority: .userInitiated) { () -> Result<String, Error> in
            do {
                let process = Process()
                process.executableURL = helperURL
                process.arguments = ["--check"]
                let output = Pipe()
                process.standardOutput = output
                process.standardError = output
                try process.run()
                let data = output.fileHandleForReading.readDataToEndOfFile()
                process.waitUntilExit()
                let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                guard process.terminationStatus == 0 else { throw CheckError.failed(text) }
                return .success(text)
            } catch {
                return .failure(error)
            }
        }.value
    }

    enum CheckError: LocalizedError {
        case failed(String)
        var errorDescription: String? {
            switch self {
            case .failed(let output): return "luna-mcp meldet einen Fehler: \(output)"
            }
        }
    }
}
