// swift-tools-version:6.1
import PackageDescription

// Gemeinsamer Kern von LunaTranscribe (App) und luna-mcp (MCP-Server):
// - LunaCore: Datenmodell, Meeting-Bibliothek, Suchindex, Aktivitätsprotokoll – nur Foundation + SQLite.
// - LunaMCPServer: die MCP-Schnittstelle (Tools, Ressourcen, Prompts) ohne Plattform-Adapter.
//   Embedding-Modell (MLX) und Bestätigungsdialog (AppKit) steckt das macOS-Target `luna-mcp` hinein.
// Beide Module bauen und testen auch unter Linux (`swift test`), damit Logik ohne Xcode prüfbar ist.
let package = Package(
    name: "LunaCore",
    platforms: [.macOS("15.0")],
    products: [
        .library(name: "LunaCore", targets: ["LunaCore"]),
        .library(name: "LunaMCPServer", targets: ["LunaMCPServer"]),
    ],
    dependencies: [
        .package(url: "https://github.com/modelcontextprotocol/swift-sdk.git", from: "0.12.1"),
        // Nur für den stdio-Test (Dateideskriptoren für StdioTransport); kommt ohnehin mit dem SDK.
        .package(url: "https://github.com/apple/swift-system.git", from: "1.0.0"),
    ],
    targets: [
        // Unter macOS kommt SQLite aus dem SDK (`import SQLite3`), unter Linux über dieses Modul.
        .systemLibrary(
            name: "CSQLite",
            pkgConfig: "sqlite3",
            providers: [.apt(["libsqlite3-dev"])]
        ),
        .target(
            name: "LunaCore",
            dependencies: [
                .target(name: "CSQLite", condition: .when(platforms: [.linux])),
            ]
        ),
        .target(
            name: "LunaMCPServer",
            dependencies: [
                "LunaCore",
                .product(name: "MCP", package: "swift-sdk"),
            ]
        ),
        .testTarget(name: "LunaCoreTests", dependencies: ["LunaCore"], exclude: ["Fixtures"]),
        .testTarget(name: "LunaMCPServerTests", dependencies: [
            "LunaMCPServer",
            "LunaCore",
            .product(name: "MCP", package: "swift-sdk"),
            .product(name: "SystemPackage", package: "swift-system"),
        ]),
    ]
)
