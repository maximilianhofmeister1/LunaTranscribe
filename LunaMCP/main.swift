import Foundation
import LunaCore
import LunaMCPServer
import MCP
import os

// luna-mcp – der MCP-Server von LunaTranscribe. Claude Code / Claude Desktop starten ihn als Kindprozess und
// sprechen JSON-RPC über stdin/stdout. Deshalb gilt: **Im Serverbetrieb gehört stdout ausschließlich JSON-RPC.**
// Kein `print` außerhalb von --help/--version/--check/--selftest; Logs über os.Logger.
//
//   luna-mcp                     MCP-Server über stdio (so tragen Claude Code und Claude Desktop ihn ein)
//   luna-mcp --check             Einrichtung prüfen (Ordner, Schalter, Index, Modell) – nur lesend
//   luna-mcp --selftest          Funktionsprobe mit Beispiel-Meetings in einem temporären Ordner
//   luna-mcp --library <pfad>    anderen Meeting-Ordner verwenden (auch: LUNA_MEETING_FOLDER)
//   luna-mcp --version | --help

let logger = Logger(subsystem: SharedSettings.appDomain, category: "mcp")
let arguments = Array(CommandLine.arguments.dropFirst())

func value(after flag: String) -> String? {
    guard let index = arguments.firstIndex(of: flag), arguments.indices.contains(index + 1) else { return nil }
    return arguments[index + 1]
}

let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
let libraryOverride = (value(after: "--library") ?? ProcessInfo.processInfo.environment["LUNA_MEETING_FOLDER"])
    .map { URL(filePath: ($0 as NSString).expandingTildeInPath, directoryHint: .isDirectory) }

/// Lädt das Embedding-Modell erst bei der ersten inhaltlichen Suche und nur, wenn es lokal liegt.
actor EmbedderProvider {
    private var embedder: MLXTextEmbedder?

    func current() -> (any TextEmbedder)? {
        guard SharedSettings.readAppDomain().semanticSearchEnabled, EmbeddingModelKind.standard.isDownloaded else { return nil }
        if embedder == nil { embedder = MLXTextEmbedder(kind: .standard, offlineOnly: true) }
        return embedder
    }
}

let embedders = EmbedderProvider()
let environment = ServerEnvironment(
    settings: { SharedSettings.readAppDomain() },
    libraryOverride: libraryOverride,
    embedder: { await embedders.current() },
    confirmer: AppKitConfirmer(),
    version: version)

func modelStatus() -> String {
    let kind = EmbeddingModelKind.standard
    return kind.isDownloaded ? "Modell \(kind.huggingFaceID) liegt lokal" : "Modell \(kind.huggingFaceID) nicht geladen"
}

func runCommand() async -> Int32 {
    if arguments.contains("--help") || arguments.contains("-h") {
        print("""
        luna-mcp \(version) – MCP-Server für die Meetings aus LunaTranscribe

          luna-mcp                     MCP-Server über stdio (für Claude Code / Claude Desktop)
          luna-mcp --check             Einrichtung prüfen (nur lesend)
          luna-mcp --selftest          Funktionsprobe mit Beispiel-Meetings
          luna-mcp --library <pfad>    anderen Meeting-Ordner verwenden
          luna-mcp --version

        Claude Code:    claude mcp add --scope user --transport stdio lunatranscribe -- \(CommandLine.arguments[0])
        """)
        return 0
    }
    if arguments.contains("--version") {
        print("luna-mcp \(version)")
        return 0
    }
    if arguments.contains("--check") {
        print(await Diagnostics.check(environment: environment, modelStatus: modelStatus()))
        return 0
    }
    if arguments.contains("--selftest") {
        let embedder = await embedders.current()
        let result = await Diagnostics.selfTest(embedder: embedder, version: version)
        print(result.report)
        print(result.passed ? "Selbsttest bestanden." : "Selbsttest FEHLGESCHLAGEN.")
        return result.passed ? 0 : 1
    }

    logger.notice("luna-mcp \(version, privacy: .public) startet (stdio)")
    do {
        try await LunaMCPServer(environment: environment).run(transport: StdioTransport())
        logger.notice("luna-mcp beendet")
        return 0
    } catch {
        logger.error("luna-mcp abgebrochen: \(error.localizedDescription, privacy: .public)")
        return 1
    }
}

// Der Haupt-Thread bleibt frei für den Bestätigungsdialog (AppKit braucht ihn); der Server läuft als Task.
Task {
    exit(await runCommand())
}
dispatchMain()
