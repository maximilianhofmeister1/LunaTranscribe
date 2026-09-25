import Foundation
import LunaCore
import Testing
@testable import LunaMCPServer

@Suite("Diagnose")
struct DiagnosticsTests {
    @Test("Selbsttest besteht ohne Embedding-Modell")
    func selfTest() async {
        let result = await Diagnostics.selfTest(embedder: nil, version: "test")
        #expect(result.passed, "\(result.report)")
        #expect(result.report.contains("✓ delete_meetings"))
        #expect(result.report.contains("Inhaltliche Suche übersprungen"))
    }

    @Test("Plausibilitätsprüfung erkennt passende und unbrauchbare Vektoren")
    func embeddingSanity() async {
        #expect(await Diagnostics.embeddingSanity(SynonymEmbedder()).0)
        #expect(await Diagnostics.embeddingSanity(ConstantEmbedder()).0 == false)
    }

    @Test("check beschreibt die Einrichtung")
    func check() async throws {
        let harness = try Harness(settings: SharedSettings(mcpAccessEnabled: true))
        try harness.add(Samples.planning)
        try harness.add(Samples.secret)
        let text = await Diagnostics.check(environment: harness.environment, modelStatus: "Modell fehlt")
        #expect(text.contains("Meetings: 2 (davon 1 vertraulich"))
        #expect(text.contains("Freigabe für Claude: an · Bearbeiten: aus"))
        #expect(text.contains("Suchindex: 1 Meetings"))
    }
}

/// Liefert für jeden Text denselben Vektor – ein kaputtes Modell.
struct ConstantEmbedder: TextEmbedder {
    let modelID = "constant"
    func embedDocuments(_ texts: [String]) async throws -> [[Float]] { texts.map { _ in [1, 0, 0] } }
    func embedQuery(_ text: String) async throws -> [Float] { [1, 0, 0] }
}
