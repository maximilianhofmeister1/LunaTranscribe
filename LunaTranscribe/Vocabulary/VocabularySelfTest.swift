import Foundation
import FluidAudio
import LunaCore

/// Entwickler-Einstieg, um das eigene Wörterbuch reproduzierbar an Audiodateien zu prüfen –
/// ohne Mikrofon, deshalb auch direkt aus dem Terminal startbar:
///
///     say -v Anna -o /tmp/luna.wav --data-format=LEF32@16000 "Ich teste LunaTranscribe mit Kubernetes"
///     LunaTranscribe --vocab-selftest /tmp/luna.wav [weitere.wav …] --terms "LunaTranscribe=Luna Transcribe;Kubernetes" --language de
///
/// `--terms`: Einträge durch `;` getrennt, Varianten nach `=` durch `,` (Standard: Wörterbuch aus den
/// Einstellungen). `--language` wie bei `--refine-selftest` (Standard: Hauptsprache). Transkribiert jede
/// Datei mit Parakeet dreimal – ohne Wörterbuch, nur mit akustischem Boosting (Varianten weggelassen)
/// und mit beiden Stufen – und druckt Texte und Dauer; die erste Zeile zeigt, was die Varianten allein bewirken.
enum VocabularySelfTest {
    static let flag = "--vocab-selftest"

    /// Startet den Selbsttest, falls das Flag gesetzt ist. Gibt `true` zurück, wenn er läuft.
    @MainActor
    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return false }
        let files = args[(index + 1)...].prefix { !$0.hasPrefix("--") }.map { URL(fileURLWithPath: $0) }
        guard !files.isEmpty else { return false }
        if let logIndex = args.firstIndex(of: "--log"), args.indices.contains(logIndex + 1) {
            freopen(args[logIndex + 1], "a", stdout)
            setvbuf(stdout, nil, _IOLBF, 0)
        }

        Task {
            do {
                try await run(files: files, args: args)
                exit(0)
            } catch {
                print("FEHLER: \(error.localizedDescription)")
                exit(1)
            }
        }
        return true
    }

    @MainActor
    private static func run(files: [URL], args: [String]) async throws {
        let settings = SettingsStore.shared
        var language = settings.primaryLanguage
        if let i = args.firstIndex(of: "--language"), args.indices.contains(i + 1), let l = AppLanguage(rawValue: args[i + 1]) { language = l }
        var entries = settings.vocabularyEntries
        if let i = args.firstIndex(of: "--terms"), args.indices.contains(i + 1) { entries = parseTerms(args[i + 1]) }

        let full = CustomVocabulary(entries: entries)
        let boostingOnly = CustomVocabulary(entries: entries.map { VocabularyEntry(term: $0.term) })
        guard !full.isEmpty else { print("Keine Begriffe – `--terms` angeben oder in den Einstellungen pflegen."); return }
        print("Sprache: \(language.displayName)")
        for entry in full.entries {
            print("  \(entry.term)" + (entry.aliases.isEmpty ? "" : " ← \(entry.aliases.joined(separator: ", "))"))
        }

        let engine = ParakeetEngine()
        var t0 = Date()
        try await engine.prepare { _ in }
        print("Parakeet geladen in \(seconds(since: t0))")
        t0 = Date()
        _ = try await engine.configureVocabulary(boostingOnly)
        print("Boosting-Modell geladen in \(seconds(since: t0))")

        let converter = AudioConverter()
        for file in files {
            let samples = try converter.resampleAudioFile(file)
            print("\n\(file.lastPathComponent) (\(String(format: "%.1f", Double(samples.count) / 16_000)) s Audio)")

            _ = try await engine.configureVocabulary(.disabled)
            let (raw, rawTime) = try await timed { try await engine.transcribe(samples, language: language) }
            print("  ohne Wörterbuch   (\(rawTime)): \(raw)")
            print("  nur Varianten              : \(full.correct(raw))")

            _ = try await engine.configureVocabulary(boostingOnly)
            let (boosted, boostedTime) = try await timed { try await engine.transcribe(samples, language: language) }
            print("  nur Boosting      (\(boostedTime)): \(boosted)")

            _ = try await engine.configureVocabulary(full)
            let (both, bothTime) = try await timed { try await engine.transcribe(samples, language: language) }
            print("  Boosting+Varianten(\(bothTime)): \(both)")
        }
    }

    /// „Begriff=Variante1,Variante2;Begriff2“ → Einträge.
    private static func parseTerms(_ text: String) -> [VocabularyEntry] {
        text.split(separator: ";").map { part in
            let pieces = part.split(separator: "=", maxSplits: 1)
            let aliases = pieces.count > 1 ? pieces[1].split(separator: ",").map(String.init) : []
            return VocabularyEntry(term: String(pieces.first ?? ""), aliases: aliases)
        }
    }

    private static func timed(_ work: () async throws -> String) async throws -> (String, String) {
        let t0 = Date()
        let result = try await work()
        return (result, seconds(since: t0))
    }

    private static func seconds(since start: Date) -> String {
        String(format: "%.2f s", Date().timeIntervalSince(start))
    }
}
