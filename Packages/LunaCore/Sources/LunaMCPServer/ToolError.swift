import Foundation

/// Fachlicher Fehler eines Tools. Wird als Tool-Ergebnis mit `isError: true` gemeldet (nicht als
/// JSON-RPC-Fehler), damit Claude die Meldung liest und sich selbst korrigieren kann – so empfiehlt es
/// die MCP-Spezifikation auch für ungültige Eingaben. Die Meldung nennt immer den nächsten Schritt.
public struct ToolError: Error, Equatable, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}
