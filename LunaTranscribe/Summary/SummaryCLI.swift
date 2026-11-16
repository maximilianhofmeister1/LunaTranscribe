import Foundation
import LunaCore

/// Eine lokal installierte Anbieter-CLI, die eine Anweisung und einen Text bekommt und nur Text
/// zurückgibt – ohne Werkzeuge, ohne Projektkontext. Neue Anbieter: `SummaryProvider`-Fall in LunaCore,
/// eine Implementierung hier und ein Fall in `SummaryProvider.locateCLI`. Einstellungen und
/// `MeetingSummarizer` brauchen sonst keine Änderung.
///
/// Achtung Local-First: Die CLI läuft lokal, das Modell aber beim Anbieter. Der Text verlässt den Mac
/// (über das Konto des Nutzers). Die Oberfläche weist darauf hin.
protocol SummaryCLI: Sendable {
    var process: CLIProcess { get }
    /// Schickt `input` mit der System-Anweisung `instructions` an das Modell und gibt die Antwort zurück.
    /// - Parameter model: Modellname der CLI; `nil` = ihr Standard.
    func complete(instructions: String, input: String, model: String?, timeout: TimeInterval) async throws -> String
}

extension SummaryCLI {
    var provider: SummaryProvider { process.provider }
    var executable: URL { process.executable }
    func version() async throws -> String { try await process.version() }
}

extension SummaryProvider {
    /// Findet die CLI dieses Anbieters (`customPath` leer = automatisch). Fragt im Zweifel die Login-Shell –
    /// nicht auf dem Main Thread aufrufen, wenn es sich vermeiden lässt.
    func locateCLI(customPath: String) -> (any SummaryCLI)? {
        guard let process = CLIProcess.locate(self, customPath: customPath) else { return nil }
        switch self {
        case .claude: return ClaudeCLI(process: process)
        case .antigravity: return AntigravityCLI(process: process)
        case .codex: return CodexCLI(process: process)
        }
    }
}

extension SummaryProvider {
    /// Einmaliger Hinweis vor der ersten Zusammenfassung mit diesem Anbieter.
    var cloudNotice: String {
        var text = "Die Transkription bleibt lokal. Für die Zusammenfassung schickt LunaTranscribe den Text des Meetings über die \(cliName) an \(vendor) – mit deinem \(account)."
        if let dataUse { text += " " + dataUse }
        return text + " Dieser Hinweis erscheint je Anbieter nur einmal."
    }

    /// Was der Anbieter laut eigener Doku mit dem Text macht, wenn das vom Üblichen abweicht.
    var dataUse: String? {
        switch self {
        case .antigravity:
            return "Google nutzt Eingaben der Antigravity CLI standardmäßig zur Produktverbesserung; abschalten kannst du das in den Antigravity-Einstellungen. Außerdem behält agy jede Zusammenfassung samt Transkript in seinem eigenen Verlauf auf diesem Mac (~/.gemini/antigravity-cli)."
        case .codex:
            return "Ob OpenAI den Text zum Training nutzt, richtet sich nach den Datenkontrollen deines ChatGPT-Kontos."
        case .claude:
            return nil
        }
    }
}

extension SummaryProvider {
    /// Hinweis unter dem Pfadfeld in den Einstellungen.
    var pathHint: String {
        var hint = "Leer = automatisch suchen. Installieren: \(installCommand), danach einmal „\(loginCommand)“ im Terminal starten und anmelden."
        if self == .claude { hint += " Gilt auch für die Nachbearbeitung mit Claude Haiku." }
        return hint
    }

    /// Hinweis unter dem Modellfeld in den Einstellungen.
    var modelHint: String {
        switch self {
        case .claude: return "Alias wie „sonnet“ oder „opus“; leer = Standard der CLI."
        case .antigravity: return "Name aus „agy models“, z. B. „gemini-3.8-flash-medium“ (schneller); leer = Standard der CLI."
        case .codex: return "Modellname wie in Codex unter /model; leer = Standard deines ChatGPT-Kontos."
        }
    }
}
