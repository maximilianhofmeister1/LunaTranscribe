import Foundation
import LunaCore
import MCP
import SystemPackage
import Testing
@testable import LunaMCPServer

/// Prüft den Weg, den Claude Code und Claude Desktop nutzen: JSON-RPC-Zeilen über stdin/stdout.
/// Wichtig: Auf stdout darf nichts außer JSON-RPC landen – jede andere Ausgabe zerstört das Protokoll.
@Suite("stdio")
struct StdioTests {
    @Test("Handshake, tools/list und tools/call über Pipes; jede Ausgabezeile ist JSON-RPC")
    func roundTrip() async throws {
        let harness = try Harness()
        try harness.add(Samples.planning)
        let input = Pipe()
        let output = Pipe()
        let transport = StdioTransport(input: FileDescriptor(rawValue: input.fileHandleForReading.fileDescriptor),
                                       output: FileDescriptor(rawValue: output.fileHandleForWriting.fileDescriptor))
        let server = LunaMCPServer(environment: harness.environment)
        let task = Task { try await server.run(transport: transport) }
        defer { task.cancel() }

        let requests = [
            #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"claude-code","version":"2.1"}}}"#,
            #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
            #"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#,
            #"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_meetings","arguments":{"query":"Angebot"}}}"#,
        ]
        for request in requests {
            input.fileHandleForWriting.write(Data((request + "\n").utf8))
        }

        var responses: [[String: Any]] = []
        var buffer = Data()
        let deadline = Date().addingTimeInterval(10)
        while responses.count < 3, Date() < deadline {
            let chunk = output.fileHandleForReading.availableData
            buffer.append(chunk)
            while let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer[buffer.startIndex ..< newline]
                buffer.removeSubrange(buffer.startIndex ... newline)
                let object = try #require(try JSONSerialization.jsonObject(with: line) as? [String: Any],
                                          "stdout enthielt etwas anderes als JSON: \(String(decoding: line, as: UTF8.self))")
                #expect(object["jsonrpc"] as? String == "2.0")
                responses.append(object)
            }
        }
        try #require(responses.count == 3)

        let initialize = try #require(responses.first { $0["id"] as? Int == 1 }?["result"] as? [String: Any])
        #expect(initialize["protocolVersion"] as? String == "2025-11-25")
        #expect((initialize["instructions"] as? String)?.contains("search_meetings") == true)
        #expect((initialize["serverInfo"] as? [String: Any])?["name"] as? String == "lunatranscribe")

        let tools = try #require((responses.first { $0["id"] as? Int == 2 }?["result"] as? [String: Any])?["tools"] as? [[String: Any]])
        #expect(tools.count == 4)

        let call = try #require(responses.first { $0["id"] as? Int == 3 }?["result"] as? [String: Any])
        let text = ((call["content"] as? [[String: Any]])?.first?["text"] as? String) ?? ""
        #expect(text.contains("Planung Q4"))
        #expect(harness.activity.first?.client == "claude-code")
    }
}
