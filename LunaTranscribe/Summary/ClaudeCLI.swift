import Foundation

/// Ruft die lokal installierte Claude CLI (`claude -p`) als reine Textverarbeitung auf: ohne Werkzeuge,
/// ohne MCP-Server, ohne Projekt- oder Nutzereinstellungen, ohne gespeicherte Sitzung. Der Text geht
/// per stdin hinein, die Antwort kommt über stdout zurück.
///
/// Achtung Local-First: Die CLI läuft lokal, das Modell aber bei Anthropic. Der übergebene Text
/// verlässt damit den Mac (über das Claude-Konto des Nutzers). Die UI weist darauf hin.
struct ClaudeCLI: Sendable {
    let executable: URL

    enum CLIError: LocalizedError {
        case notFound
        case failed(status: Int32, message: String)
        case timedOut(TimeInterval)
        case emptyOutput

        var errorDescription: String? {
            switch self {
            case .notFound:
                return "Die Claude CLI wurde nicht gefunden. Installieren: curl -fsSL https://claude.ai/install.sh | bash – oder den Pfad in den Einstellungen → Meetings angeben."
            case .failed(let status, let message):
                let detail = message.trimmingCharacters(in: .whitespacesAndNewlines)
                if detail.localizedCaseInsensitiveContains("login") || detail.localizedCaseInsensitiveContains("auth") {
                    return "Die Claude CLI ist nicht angemeldet. Im Terminal einmal „claude“ starten und anmelden. (\(detail))"
                }
                return "Die Claude CLI ist mit Code \(status) abgebrochen: \(detail.isEmpty ? "keine Meldung" : String(detail.prefix(300)))"
            case .timedOut(let seconds):
                return "Die Claude CLI hat nach \(Int(seconds)) s nicht geantwortet."
            case .emptyOutput:
                return "Die Claude CLI hat eine leere Antwort geliefert."
            }
        }
    }

    // MARK: Finden

    /// Sucht die CLI: eingestellter Pfad → übliche Installationsorte → `command -v` in der Login-Shell.
    /// GUI-Apps erben den `PATH` der Shell nicht, deshalb die festen Orte zuerst.
    static func locate(customPath: String) -> ClaudeCLI? {
        let fm = FileManager.default
        let custom = (customPath as NSString).expandingTildeInPath
        if !custom.isEmpty {
            return fm.isExecutableFile(atPath: custom) ? ClaudeCLI(executable: URL(filePath: custom)) : nil
        }
        let home = fm.homeDirectoryForCurrentUser.path
        let candidates = ["\(home)/.local/bin/claude", "\(home)/.claude/local/claude",
                          "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]
        if let path = candidates.first(where: fm.isExecutableFile(atPath:)) {
            return ClaudeCLI(executable: URL(filePath: path))
        }
        return loginShellLookup().map { ClaudeCLI(executable: URL(filePath: $0)) }
    }

    private static func loginShellLookup() -> String? {
        let process = Process()
        process.executableURL = URL(filePath: "/bin/zsh")
        process.arguments = ["-lc", "command -v claude"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        process.waitUntilExit()
        let path = String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return process.terminationStatus == 0 && FileManager.default.isExecutableFile(atPath: path) ? path : nil
    }

    // MARK: Aufrufen

    func version() async throws -> String {
        try await execute(arguments: ["--version"], input: nil, timeout: 20)
    }

    /// Schickt `input` mit der System-Anweisung `instructions` an Claude und gibt die Antwort zurück.
    func complete(instructions: String, input: String, model: String?, timeout: TimeInterval = 300) async throws -> String {
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
        let output = try await execute(arguments: arguments, input: input, timeout: timeout)
        guard !output.isEmpty else { throw CLIError.emptyOutput }
        return output
    }

    /// Startet den Prozess in einem leeren Arbeitsordner (kein CLAUDE.md, kein Projektkontext) und
    /// liest stdout/stderr fortlaufend, damit große Antworten die Pipe nicht blockieren.
    private func execute(arguments: [String], input: String?, timeout: TimeInterval) async throws -> String {
        try Task.checkCancellation()
        let workDir = FileManager.default.temporaryDirectory.appending(path: "LunaTranscribe-claude-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: workDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: workDir) }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = workDir
        var environment = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        environment["PATH"] = ["\(home)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",
                               environment["PATH"] ?? ""].joined(separator: ":")
        // Ohne USER/LOGNAME findet die CLI ihre Anmeldung im Schlüsselbund nicht.
        if environment["USER"] == nil { environment["USER"] = NSUserName() }
        if environment["LOGNAME"] == nil { environment["LOGNAME"] = NSUserName() }
        process.environment = environment

        let stdout = Pipe(), stderr = Pipe(), stdin = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        process.standardInput = input == nil ? FileHandle.nullDevice : stdin

        let collector = OutputCollector()
        stdout.fileHandleForReading.readabilityHandler = { collector.appendOut($0.availableData) }
        stderr.fileHandleForReading.readabilityHandler = { collector.appendErr($0.availableData) }

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
                let timer = DispatchWorkItem { [process] in
                    collector.markTimedOut()
                    if process.isRunning { process.terminate() }
                }
                process.terminationHandler = { process in
                    timer.cancel()
                    stdout.fileHandleForReading.readabilityHandler = nil
                    stderr.fileHandleForReading.readabilityHandler = nil
                    collector.appendOut(stdout.fileHandleForReading.readDataToEndOfFile())
                    collector.appendErr(stderr.fileHandleForReading.readDataToEndOfFile())
                    let (out, err, timedOut) = collector.result()
                    if timedOut {
                        continuation.resume(throwing: CLIError.timedOut(timeout))
                    } else if process.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: CancellationError())
                    } else if process.terminationStatus != 0 {
                        continuation.resume(throwing: CLIError.failed(status: process.terminationStatus, message: err.isEmpty ? out : err))
                    } else {
                        continuation.resume(returning: out.trimmingCharacters(in: .whitespacesAndNewlines))
                    }
                }
                do {
                    try process.run()
                } catch {
                    continuation.resume(throwing: error)
                    return
                }
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: timer)
                if let input {
                    // Eigener Thread: Schreiben blockiert, bis die CLI liest.
                    DispatchQueue.global(qos: .userInitiated).async {
                        stdin.fileHandleForWriting.write(Data(input.utf8))
                        try? stdin.fileHandleForWriting.close()
                    }
                }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }
    }
}

/// Sammelt die Ausgaben threadsicher (Readability-Handler laufen auf beliebigen Threads).
private final class OutputCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var out = Data()
    private var err = Data()
    private var timedOut = false

    func appendOut(_ data: Data) { lock.withLock { out.append(data) } }
    func appendErr(_ data: Data) { lock.withLock { err.append(data) } }
    func markTimedOut() { lock.withLock { timedOut = true } }

    func result() -> (String, String, Bool) {
        lock.withLock { (String(decoding: out, as: UTF8.self), String(decoding: err, as: UTF8.self), timedOut) }
    }
}
