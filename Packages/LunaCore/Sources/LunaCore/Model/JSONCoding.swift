import Foundation

/// Einheitliche JSON-Einstellungen für alle Dateien, die App und luna-mcp teilen (ISO-8601-Daten).
/// Frische Instanzen pro Aufruf: Coder sind billig und so ohne Absprache zwischen Threads nutzbar.
public enum JSONCoding {
    public static func encoder(pretty: Bool = true) -> JSONEncoder {
        let e = JSONEncoder()
        e.outputFormatting = pretty ? [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes] : [.sortedKeys, .withoutEscapingSlashes]
        e.dateEncodingStrategy = .iso8601
        return e
    }

    public static func decoder() -> JSONDecoder {
        let d = JSONDecoder()
        d.dateDecodingStrategy = .iso8601
        return d
    }
}
