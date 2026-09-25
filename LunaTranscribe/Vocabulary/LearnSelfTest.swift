import Foundation

/// Entwickler-Einstieg für das lernende Wörterbuch – ohne Modell und ohne Berechtigung:
///
///     LunaTranscribe --learn-selftest                       # eingebaute Fälle, prüft Erwartungen
///     LunaTranscribe --learn-selftest "vorher" "nachher"    # eigener Fall, druckt die Kandidaten
///
/// Die eingebauten Fälle decken Verhörer (sollen Vorschläge werden) und Umformulierungen,
/// Satzanfänge und Zeichensetzung ab (sollen es nicht).
enum LearnSelfTest {
    static let flag = "--learn-selftest"

    private struct Case {
        let before: String
        let after: String
        let expected: [CorrectionLearner.Candidate]
    }

    private static let cases: [Case] = [
        Case(before: "Ich schicke dir den Export aus Weglap.", after: "Ich schicke dir den Export aus weclapp.",
             expected: [.init(variant: "Weglap", term: "weclapp")]),
        Case(before: "Heute habe ich Luna Transcribe getestet", after: "Heute habe ich LunaTranscribe getestet",
             expected: [.init(variant: "Luna Transcribe", term: "LunaTranscribe")]),
        Case(before: "Wir deployen auf Cubernetes.", after: "Wir deployen auf Kubernetes.",
             expected: [.init(variant: "Cubernetes", term: "Kubernetes")]),
        Case(before: "Das Meeting mit Maximillian ist morgen", after: "Das Meeting mit Maximilian ist morgen",
             expected: [.init(variant: "Maximillian", term: "Maximilian")]),
        // Umformulierung: kein Verhörer
        Case(before: "Das finde ich gut.", after: "Das finde ich hervorragend.", expected: []),
        // Funktionswort getauscht
        Case(before: "Ich gehe in die Stadt.", after: "Ich gehe in der Stadt.", expected: []),
        // Satzanfang groß geschrieben
        Case(before: "morgen passt es mir.", after: "Morgen passt es mir.", expected: []),
        // Nur Zeichensetzung
        Case(before: "Ja das passt", after: "Ja, das passt.", expected: []),
        // Großschreibung mitten im Satz: Marke
        Case(before: "Mein neues Iphone ist da", after: "Mein neues iPhone ist da",
             expected: [.init(variant: "Iphone", term: "iPhone")]),
        // Beugung eines normalen Worts: Grammatik, kein Verhörer
        Case(before: "Dann klappen wir das Buch zu", after: "Dann klappten wir das Buch zu", expected: []),
        // Ganzer Satz umgeschrieben: zu lang für einen Verhörer
        Case(before: "Kannst du das bitte bis morgen erledigen", after: "Schaffst du es vielleicht noch heute Abend", expected: []),
    ]

    /// Startet den Selbsttest, falls das Flag gesetzt ist. Gibt `true` zurück, wenn er läuft.
    static func runIfRequested() -> Bool {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: flag) else { return false }
        let own = Array(args[(index + 1)...].prefix { !$0.hasPrefix("--") })

        if own.count >= 2 {
            let found = CorrectionLearner.candidates(before: own[0], after: own[1])
            print(found.isEmpty ? "Keine Kandidaten." : found.map { "„\($0.variant)“ → „\($0.term)“" }.joined(separator: "\n"))
            exit(0)
        }

        var failures = 0
        for c in cases {
            let found = CorrectionLearner.candidates(before: c.before, after: c.after)
            let ok = found == c.expected
            if !ok { failures += 1 }
            let shown = found.isEmpty ? "–" : found.map { "\($0.variant) → \($0.term)" }.joined(separator: ", ")
            print("\(ok ? "OK  " : "FAIL") „\(c.before)“ → „\(c.after)“: \(shown)")
        }
        print("\n\(cases.count - failures)/\(cases.count) Fälle wie erwartet.")
        exit(failures == 0 ? 0 : 1)
    }
}
