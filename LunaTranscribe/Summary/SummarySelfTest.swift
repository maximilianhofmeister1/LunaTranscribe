import Foundation

/// Entwickler-Einstieg für die Zusammenfassung über die Claude CLI – über denselben Code wie der
/// Button in der App (`ClaudeCLI`, `SummaryPrompt`), ohne Modelle und ohne Berechtigungen:
///
///     LunaTranscribe --summary-selftest <Meeting-Ordner/meeting.json> [--cli /pfad/zu/claude] [--model sonnet]
///
/// Ohne Datei fasst der Test ein kurzes eingebautes Gespräch zusammen. `--cli` mit einem falschen Pfad
/// prüft die Fehlermeldung „CLI nicht gefunden“. Achtung: Der Text geht an Anthropic.
enum SummarySelfTest {
    static let flag = "--summary-selftest"

    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return false }
        let file = args.indices.contains(index + 1) && !args[index + 1].hasPrefix("--") ? args[index + 1] : nil
        let cliPath = value(after: "--cli", in: args) ?? ""
        let model = value(after: "--model", in: args)

        Task {
            do {
                let transcript = try file.map(load) ?? sample
                guard let cli = ClaudeCLI.locate(customPath: cliPath) else { throw ClaudeCLI.CLIError.notFound }
                print("CLI: \(cli.executable.path) · \(try await cli.version())")
                print("Meeting: \(transcript.title), \(transcript.turns.count) Beiträge, \(transcript.wordCount) Wörter\n")
                let started = Date()
                let summary = try await cli.complete(
                    instructions: SummaryPrompt.instructions(template: SummaryPrompt.defaultTemplate, language: transcript.language),
                    input: transcript.render(as: .text), model: model)
                print(summary)
                print(String(format: "\n(%.1f s)", Date().timeIntervalSince(started)))
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                exit(1)
            }
        }
        return true
    }

    private static func value(after flag: String, in args: [String]) -> String? {
        guard let i = args.firstIndex(of: flag), args.indices.contains(i + 1) else { return nil }
        return args[i + 1]
    }

    private static func load(_ path: String) throws -> MeetingTranscript {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(MeetingTranscript.self, from: Data(contentsOf: URL(filePath: path)))
    }

    private static var sample: MeetingTranscript {
        let lines = [
            ("Anna", "Guten Morgen. Heute geht es um den Launch der neuen Version."),
            ("Markus", "Die Tests laufen noch, ich schlage Montag statt Freitag vor."),
            ("Anna", "Einverstanden. Schickst du bis Freitag den Testbericht?"),
            ("Markus", "Ja. Offen ist noch, wer die Release Notes schreibt."),
        ]
        let turns = lines.enumerated().map { i, line in
            MeetingTranscript.Turn(speakerID: line.0, start: TimeInterval(i * 10), end: TimeInterval(i * 10 + 9), text: line.1)
        }
        return MeetingTranscript(title: "Selbsttest", date: Date(), durationSeconds: 40, engine: .parakeet, language: .german, turns: turns)
    }
}
