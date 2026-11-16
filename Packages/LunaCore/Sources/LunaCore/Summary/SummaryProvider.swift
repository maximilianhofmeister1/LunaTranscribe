import Foundation

/// Über welche lokal installierte CLI eine Meeting-Zusammenfassung entsteht. Alle drei laufen mit dem
/// Konto bzw. Abo des Nutzers (kein API-Schlüssel), schicken den Text aber an den jeweiligen Anbieter.
///
/// Rohwerte sind gespeicherte Daten (Einstellung `summaryProvider`, Feld `provider` in `meeting.json`) –
/// nie ändern. Unbekannte Werte (z. B. aus einer neueren Version) gelten beim Lesen als „nicht gesetzt“.
public enum SummaryProvider: String, CaseIterable, Identifiable, Sendable {
    case claude
    case antigravity
    case codex

    public var id: String { rawValue }

    /// Kurzer Name für Auswahl und „Erstellt mit …“.
    public var label: String {
        switch self {
        case .claude: return "Claude"
        case .antigravity: return "Gemini"
        case .codex: return "ChatGPT"
        }
    }

    /// Name des Kommandozeilenprogramms, wie ihn Hinweise und Fehlermeldungen nennen.
    public var cliName: String {
        switch self {
        case .claude: return "Claude CLI"
        case .antigravity: return "Antigravity CLI"
        case .codex: return "Codex CLI"
        }
    }

    /// Befehl im `PATH`.
    public var command: String {
        switch self {
        case .claude: return "claude"
        case .antigravity: return "agy"
        case .codex: return "codex"
        }
    }

    /// Wer den Text verarbeitet – für den Cloud-Hinweis.
    public var vendor: String {
        switch self {
        case .claude: return "Anthropic"
        case .antigravity: return "Google"
        case .codex: return "OpenAI"
        }
    }

    /// Das Konto, mit dem die CLI angemeldet ist.
    public var account: String {
        switch self {
        case .claude: return "Claude-Konto"
        case .antigravity: return "Google-Konto"
        case .codex: return "ChatGPT-Konto"
        }
    }
}
