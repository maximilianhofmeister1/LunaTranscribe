import Foundation

/// Entwickler-Einstieg ohne Sprechen, um Prompt und Modell der Nachbearbeitung reproduzierbar zu prüfen:
///
///     open -n /Applications/LunaTranscribe.app --args --refine-selftest "ähm also ich wollte sagen dass dass wir morgen um um zehn uhr nein um elf uhr das meeting haben" --log /tmp/luna-refine.log
///
/// Optional `--model lfm2_1_2b|qwen3_1_7b|appleIntelligence` (Standard: Einstellung),
/// `--language de|en|…` (Standard: Hauptsprache) und `--prompt <datei>` (Anweisung mit `{language}`,
/// Standard: Einstellung) zum Vergleichen von Prompt-Varianten. Lädt das Modell, bereinigt den Text mehrmals
/// (erster Lauf = kalt, weitere = warm), druckt Ergebnis und Dauer und beendet den Prozess.
enum RefineSelfTest {
    static let flag = "--refine-selftest"

    /// Startet den Selbsttest, falls das Flag gesetzt ist. Gibt `true` zurück, wenn er läuft.
    @MainActor
    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag), args.indices.contains(index + 1) else { return false }
        let text = args[index + 1]
        // `--log <Pfad>`: Ausgabe in eine Datei, wenn die App über LaunchServices (`open --args …`) startet.
        if let logIndex = args.firstIndex(of: "--log"), args.indices.contains(logIndex + 1) {
            freopen(args[logIndex + 1], "a", stdout)
            setvbuf(stdout, nil, _IOLBF, 0)
        }

        Task {
            do {
                try await run(text: text, args: args)
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                exit(1)
            }
        }
        return true
    }

    @MainActor
    private static func run(text: String, args: [String]) async throws {
        let settings = SettingsStore.shared
        var kind = settings.refinerKind
        if let i = args.firstIndex(of: "--model"), args.indices.contains(i + 1), let k = RefinerKind(rawValue: args[i + 1]) { kind = k }
        var language = settings.primaryLanguage
        if let i = args.firstIndex(of: "--language"), args.indices.contains(i + 1), let l = AppLanguage(rawValue: args[i + 1]) { language = l }

        var template = settings.refinePrompt
        if let i = args.firstIndex(of: "--prompt"), args.indices.contains(i + 1) {
            template = try String(contentsOfFile: args[i + 1], encoding: .utf8)
        }

        print("Modell: \(kind.shortLabel), Sprache: \(language.displayName)")
        if case .unavailable(let reason) = kind.availability { print("Nicht verfügbar: \(reason)"); return }

        let manager = RefinerManager()
        manager.ensureLoaded(kind)
        let t0 = Date()
        while !manager.isReady {
            if case .failed(let message) = manager.state { throw RefinerError.unavailable(message) }
            try await Task.sleep(for: .milliseconds(200))
        }
        print("Geladen in \(String(format: "%.1f", Date().timeIntervalSince(t0))) s")
        guard let refiner = manager.current else { throw RefinerError.notLoaded }

        print("\nEingabe:  \(text)")
        for pass in 1...3 {
            let t1 = Date()
            let result = try await refiner.refine(text, language: language, promptTemplate: template)
            print("Ausgabe (\(pass == 1 ? "kalt" : "warm"), \(String(format: "%.2f", Date().timeIntervalSince(t1))) s): \(result)")
        }
    }
}
