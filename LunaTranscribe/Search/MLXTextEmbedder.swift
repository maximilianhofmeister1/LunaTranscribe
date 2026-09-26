import Foundation
import Hub
import LunaCore
import MLX
import MLXEmbedders
import Tokenizers

/// Embedding-Modell für die inhaltliche Suche über MLX (GPU, Unified Memory). Gemeinsam genutzt von der App
/// (lädt das Modell herunter, indexiert im Hintergrund) und von luna-mcp (nur offline, rechnet Anfragen).
///
/// Details, die leicht übersehen werden:
/// - E5 erwartet die Präfixe „query: “ / „passage: “.
/// - XLM-RoBERTa zählt Positionen ab 2; MLXEmbedders setzt ohne Angabe 0 – deshalb explizite `positionIds`.
/// - Mittelwert über die echten Token (Maske), dann L2-Normalisierung – wie sentence-transformers.
actor MLXTextEmbedder: TextEmbedder {
    nonisolated let modelID: String
    let kind: EmbeddingModelKind
    /// luna-mcp darf nichts herunterladen: dann nur aus dem lokalen Ordner laden.
    private let offlineOnly: Bool
    private var container: MLXEmbedders.ModelContainer?
    /// Stapelgröße beim Indexieren; klein genug für wenig Speicher, groß genug für die GPU.
    static let batchSize = 16

    init(kind: EmbeddingModelKind = .standard, offlineOnly: Bool) {
        self.kind = kind
        self.modelID = kind.huggingFaceID
        self.offlineOnly = offlineOnly
    }

    var isLoaded: Bool { container != nil }

    /// Lädt das Modell (in der App beim ersten Mal von Hugging Face). `progress` zwischen 0 und 1.
    func prepare(progress: @escaping @Sendable (Double) -> Void = { _ in }) async throws {
        if container != nil { return }
        // Wie bei der Nachbearbeitung: kleiner Metal-Puffer-Cache, freier Speicher geht an andere Apps zurück.
        MLX.GPU.set(cacheLimit: 20 * 1024 * 1024)
        let hub = HubApi(downloadBase: AppPaths.models)
        let configuration: MLXEmbedders.ModelConfiguration
        if offlineOnly || kind.isDownloaded {
            guard kind.isDownloaded else { throw EmbedderError.notDownloaded }
            configuration = MLXEmbedders.ModelConfiguration(directory: kind.localFolder)
        } else {
            configuration = MLXEmbedders.ModelConfiguration(id: kind.huggingFaceID)
        }
        container = try await MLXEmbedders.loadModelContainer(hub: hub, configuration: configuration) { p in
            progress(p.fractionCompleted)
        }
        progress(1)
    }

    func unload() {
        container = nil
        MLX.GPU.clearCache()
    }

    func embedDocuments(_ texts: [String]) async throws -> [[Float]] {
        var result: [[Float]] = []
        for start in stride(from: 0, to: texts.count, by: Self.batchSize) {
            let batch = texts[start ..< min(start + Self.batchSize, texts.count)].map { kind.documentPrefix + $0 }
            result += try await embed(Array(batch))
        }
        return result
    }

    func embedQuery(_ text: String) async throws -> [Float] {
        try await embed([kind.queryPrefix + text]).first ?? []
    }

    private func embed(_ texts: [String]) async throws -> [[Float]] {
        guard !texts.isEmpty else { return [] }
        try await prepare()
        guard let container else { throw EmbedderError.notDownloaded }
        let maxTokens = kind.maxTokens
        let positionOffset = kind.positionOffset

        return await container.perform { model, tokenizer, _ in
            let tokens = texts.map { Array(tokenizer.encode(text: $0, addSpecialTokens: true).prefix(maxTokens)) }
            let length = tokens.map(\.count).max() ?? 1
            // Rechts auffüllen; die Maske blendet das Auffüllen aus, der Füllwert ist daher egal.
            let inputs = stacked(tokens.map { MLXArray($0 + Array(repeating: 0, count: length - $0.count)) })
            let mask = stacked(tokens.map { row in
                MLXArray((0 ..< length).map { $0 < row.count ? Float(1) : Float(0) })
            })
            let positions = broadcast(MLXArray((0 ..< length).map { $0 + positionOffset }), to: inputs.shape)
            let output = model(inputs, positionIds: positions, tokenTypeIds: MLXArray.zeros(like: inputs), attentionMask: mask)
            // Float32 erzwingen: `asArray(Float.self)` setzt den passenden Datentyp voraus (Gewichte evtl. Float16).
            let pooled = Pooling(strategy: .mean)(output, mask: mask, normalize: true).asType(.float32)
            pooled.eval()
            return (0 ..< texts.count).map { pooled[$0].asArray(Float.self) }
        }
    }

    enum EmbedderError: LocalizedError {
        case notDownloaded

        var errorDescription: String? {
            "Das Modell für die inhaltliche Suche ist noch nicht geladen (LunaTranscribe → Einstellungen → Meetings)."
        }
    }
}
