import Foundation

/// Rechnet Texte in Vektoren um (inhaltliche Suche). Die echte Implementierung (MLX, Metal) steckt in
/// App und luna-mcp; der Kern kennt nur diese Schnittstelle – Tests nutzen einen einfachen Ersatz.
///
/// Viele Modelle unterscheiden Anfrage und Dokument (E5: „query: “ / „passage: “); darum zwei Methoden.
/// Ergebnisse sollen L2-normalisiert sein; der Index normalisiert zur Sicherheit noch einmal.
public protocol TextEmbedder: Sendable {
    /// Eindeutiger Modellname; wechselt er, werden alle Vektoren neu berechnet.
    var modelID: String { get }
    func embedDocuments(_ texts: [String]) async throws -> [[Float]]
    func embedQuery(_ text: String) async throws -> [Float]
}

enum VectorMath {
    static func normalized(_ vector: [Float]) -> [Float] {
        let norm = vector.reduce(0) { $0 + $1 * $1 }.squareRoot()
        guard norm > 0 else { return vector }
        return vector.map { $0 / norm }
    }

    static func data(_ vector: [Float]) -> Data {
        vector.withUnsafeBufferPointer { Data(buffer: $0) }
    }

    /// Kopiert statt umzudeuten: Blobs aus SQLite sind nicht zwingend für `Float` ausgerichtet.
    static func vector(_ data: Data) -> [Float] {
        let count = data.count / MemoryLayout<Float>.stride
        return [Float](unsafeUninitializedCapacity: count) { buffer, initialized in
            initialized = data.copyBytes(to: buffer) / MemoryLayout<Float>.stride
        }
    }
}
