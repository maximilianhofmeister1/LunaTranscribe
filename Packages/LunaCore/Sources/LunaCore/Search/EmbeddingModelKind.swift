import Foundation

/// Die Embedding-Modelle für die inhaltliche Suche. Nur Daten – geladen und gerechnet wird in
/// App und luna-mcp (MLX, `MLXTextEmbedder`).
public enum EmbeddingModelKind: String, CaseIterable, Sendable {
    /// intfloat/multilingual-e5-small: MIT, ~118 M Parameter, 384 Dimensionen, rund 100 Sprachen.
    /// Klein genug, um neben ASR und Nachbearbeitung dauerhaft geladen zu bleiben.
    case multilingualE5Small = "intfloat/multilingual-e5-small"

    public static let standard = EmbeddingModelKind.multilingualE5Small

    public var huggingFaceID: String { rawValue }

    public var label: String {
        switch self {
        case .multilingualE5Small: return "Multilingual E5 Small (~470 MB)"
        }
    }

    /// E5 unterscheidet Anfrage und Dokument über ein Präfix (so trainiert).
    public var queryPrefix: String { "query: " }
    public var documentPrefix: String { "passage: " }

    /// Längere Eingaben werden abgeschnitten (Positions-Embeddings des Modells).
    public var maxTokens: Int { 512 }

    /// XLM-RoBERTa zählt Positionen ab `padding_idx + 1` = 2, nicht ab 0 wie BERT. MLXEmbedders setzt
    /// ohne Angabe 0-basierte Positionen – das würde alle Vektoren still verschieben.
    public var positionOffset: Int { 2 }

    /// Ablage nach Hugging-Face-Konvention unter `AppPaths.models` (wie die Sprachmodelle der Nachbearbeitung).
    public var localFolder: URL {
        AppPaths.models.appending(path: "models/\(huggingFaceID)", directoryHint: .isDirectory)
    }

    /// Liegt das Modell vollständig lokal? luna-mcp lädt nie selbst herunter.
    public var isDownloaded: Bool {
        let fm = FileManager.default
        let required = ["config.json", "tokenizer.json", "tokenizer_config.json"]
        guard required.allSatisfy({ fm.fileExists(atPath: localFolder.appending(path: $0).path) }),
              let files = try? fm.contentsOfDirectory(atPath: localFolder.path) else { return false }
        return files.contains { $0.hasSuffix(".safetensors") }
    }
}
