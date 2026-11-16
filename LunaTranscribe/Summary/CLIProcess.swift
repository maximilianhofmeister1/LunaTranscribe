import Foundation
import LunaCore

/// Fehler beim Aufruf einer der Anbieter-CLIs (Claude, Antigravity, Codex). Die Meldungen nennen das
/// jeweilige Programm und den nächsten Schritt, weil sie unverändert in der Oberfläche landen.
enum CLIError: LocalizedError {
    case notFound(SummaryProvider)
    case notLoggedIn(SummaryProvider, detail: String)
    case failed(SummaryProvider, status: Int32, message: String)
    case timedOut(SummaryProvider, TimeInterval)
    case emptyOutput(SummaryProvider)

    var errorDescription: String? {
        switch self {
        case .notFound(let provider):
            return "Die \(provider.cliName) wurde nicht gefunden. Installieren: \(provider.installCommand) – oder den Pfad in den Einstellungen → Meetings angeben."
        case .notLoggedIn(let provider, let detail):
            return "Die \(provider.cliName) ist nicht angemeldet. Im Terminal einmal „\(provider.loginCommand)“ starten und anmelden. (\(String(detail.prefix(200))))"
        case .failed(let provider, let status, let message):
            let detail = message.trimmingCharacters(in: .whitespacesAndNewlines)
            return "Die \(provider.cliName) ist mit Code \(status) abgebrochen: \(detail.isEmpty ? "keine Meldung" : String(detail.prefix(300)))"
        case .timedOut(let provider, let seconds):
            return "Die \(provider.cliName) hat nach \(Int(seconds)) s nicht geantwortet."
        case .emptyOutput(let provider):
            return "Die \(provider.cliName) hat eine leere Antwort geliefert."
        }
    }
}

extension SummaryProvider {
    /// Offizieller Installationsweg laut Anbieter-Doku.
    var installCommand: String {
        switch self {
        case .claude: return "curl -fsSL https://claude.ai/install.sh | bash"
        case .antigravity: return "curl -fsSL https://antigravity.google/cli/install.sh | bash"
        case .codex: return "npm install -g @openai/codex (oder brew install --cask codex)"
        }
    }

    /// Befehl zum einmaligen Anmelden im Terminal.
    var loginCommand: String {
        switch self {
        case .claude: return "claude"
        case .antigravity: return "agy"
        case .codex: return "codex login"
        }
    }
}

/// Startet eine lokal installierte CLI als reine Textverarbeitung: leerer Arbeitsordner (kein CLAUDE.md,
/// AGENTS.md oder GEMINI.md aus einem Projekt), Text per stdin, Antwort über stdout. stdout und stderr
/// werden fortlaufend gelesen, damit große Antworten die Pipe nicht blockieren.
struct CLIProcess: Sendable {
    let provider: SummaryProvider
    let executable: URL

    // MARK: Finden

    /// Sucht die CLI: eingestellter Pfad → übliche Installationsorte → `command -v` in der Login-Shell.
    /// GUI-Apps erben den `PATH` der Shell nicht, deshalb die festen Orte zuerst.
    static func locate(_ provider: SummaryProvider, customPath: String) -> CLIProcess? {
        let fm = FileManager.default
        let custom = (customPath as NSString).expandingTildeInPath
        if !custom.isEmpty {
            return fm.isExecutableFile(atPath: custom) ? CLIProcess(provider: provider, executable: URL(filePath: custom)) : nil
        }
        if let path = candidates(for: provider).first(where: fm.isExecutableFile(atPath:)) {
            return CLIProcess(provider: provider, executable: URL(filePath: path))
        }
        return loginShellLookup(provider.command).map { CLIProcess(provider: provider, executable: URL(filePath: $0)) }
    }

    private static func candidates(for provider: SummaryProvider) -> [String] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let command = provider.command
        var paths = ["\(home)/.local/bin/\(command)"]
        switch provider {
        case .claude: paths.append("\(home)/.claude/local/claude")
        case .antigravity: paths += ["\(home)/.antigravity/bin/agy", "\(home)/.gemini/antigravity-cli/bin/agy"]
        case .codex: paths += ["\(home)/.npm-global/bin/codex", "\(home)/.bun/bin/codex"] + nvmBinaries(command, home: home)
        }
        return paths + ["/opt/homebrew/bin/\(command)", "/usr/local/bin/\(command)"]
    }

    /// npm-Pakete unter nvm liegen je Node-Version in einem eigenen Ordner; die Login-Shell findet sie
    /// nicht, weil nvm meist erst in `.zshrc` geladen wird. Neueste Version zuerst.
    private static func nvmBinaries(_ command: String, home: String) -> [String] {
        let versions = URL(filePath: "\(home)/.nvm/versions/node")
        let dirs = (try? FileManager.default.contentsOfDirectory(atPath: versions.path)) ?? []
        return dirs.sorted { $0.localizedStandardCompare($1) == .orderedDescending }
            .map { versions.appending(path: "\($0)/bin/\(command)").path }
    }

    private static func loginShellLookup(_ command: String) -> String? {
        let process = Process()
        process.executableURL = URL(filePath: "/bin/zsh")
        process.arguments = ["-lc", "command -v \(command)"]
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
        try await run(arguments: ["--version"], input: nil, timeout: 20)
    }

    /// Startet den Prozess in einem leeren, danach gelöschten Arbeitsordner und gibt stdout zurück.
    func run(arguments: [String], input: String?, timeout: TimeInterval) async throws -> String {
        try Task.checkCancellation()
        let workDir = FileManager.default.temporaryDirectory.appending(path: "LunaTranscribe-\(provider.command)-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: workDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: workDir) }

        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.currentDirectoryURL = workDir
        process.environment = environment()

        let stdout = Pipe(), stderr = Pipe(), stdin = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        process.standardInput = input == nil ? FileHandle.nullDevice : stdin

        let collector = OutputCollector()
        stdout.fileHandleForReading.readabilityHandler = { collector.appendOut($0.availableData) }
        stderr.fileHandleForReading.readabilityHandler = { collector.appendErr($0.availableData) }

        let provider = provider
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
                        continuation.resume(throwing: CLIError.timedOut(provider, timeout))
                    } else if process.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: CancellationError())
                    } else if process.terminationStatus != 0 {
                        continuation.resume(throwing: Self.classify(provider, status: process.terminationStatus,
                                                                    message: err.isEmpty ? out : err))
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

    private func environment() -> [String: String] {
        var environment = ProcessInfo.processInfo.environment
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        // Der Ordner der CLI zuerst: Eine per npm (nvm) installierte Codex CLI ist ein Node-Skript und
        // braucht das `node` daneben.
        environment["PATH"] = [executable.deletingLastPathComponent().path, "\(home)/.local/bin", "/opt/homebrew/bin",
                               "/usr/local/bin", "/usr/bin", "/bin", environment["PATH"] ?? ""].joined(separator: ":")
        // Ohne USER/LOGNAME finden die CLIs ihre Anmeldung im Schlüsselbund nicht.
        if environment["USER"] == nil { environment["USER"] = NSUserName() }
        if environment["LOGNAME"] == nil { environment["LOGNAME"] = NSUserName() }
        environment["NO_COLOR"] = "1"
        return environment
    }

    /// Anmeldefehler bekommen eine eigene Meldung mit dem Login-Befehl; alles andere geht durch.
    private static func classify(_ provider: SummaryProvider, status: Int32, message: String) -> CLIError {
        let detail = message.trimmingCharacters(in: .whitespacesAndNewlines)
        let lowered = detail.lowercased()
        let loginHints = ["not logged in", "log in", "login", "authentication required", "unauthorized", "auth"]
        if loginHints.contains(where: lowered.contains) {
            return .notLoggedIn(provider, detail: detail)
        }
        return .failed(provider, status: status, message: detail)
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
