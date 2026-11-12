import Foundation
import Testing
@testable import LunaCore

@Suite("Zusammenfassung: Sprache und Bearbeitung")
struct SummaryTests {
    @Test("Sprachwahl: Rohwerte sind stabil, unbekannte Werte werden abgelehnt")
    func summaryLanguageRawValues() {
        #expect(SummaryLanguage.meeting.rawValue == "meeting")
        #expect(SummaryLanguage.fixed(.english).rawValue == "en")
        #expect(SummaryLanguage(rawValue: "meeting") == .meeting)
        #expect(SummaryLanguage(rawValue: "fr") == .fixed(.french))
        #expect(SummaryLanguage(rawValue: "klingonisch") == nil)
        #expect(AppLanguage.sortedByDisplayName.prefix(3) == [.arabic, .bulgarian, .chinese])
    }

    @Test("Sprachwahl: „wie das Meeting“ folgt dem Meeting, eine feste Sprache nicht")
    func resolvesLanguage() {
        #expect(SummaryLanguage.meeting.resolved(for: .german) == .german)
        #expect(SummaryLanguage.fixed(.english).resolved(for: .german) == .english)
    }

    @Test("Die Anweisung nennt die Zielsprache, auch für Überschriften")
    func promptUsesTargetLanguage() {
        let instructions = SummaryPrompt.instructions(template: SummaryPrompt.defaultTemplate, language: .english)
        #expect(instructions.contains("auf Englisch"))
        #expect(!instructions.contains(SummaryPrompt.placeholder))
    }

    @Test("Überschriften: bekannte Sprachen fest übersetzt, Deutsch und andere mit Bitte, eigene Anweisung bleibt")
    func localizesHeadings() {
        let english = SummaryPrompt.instructions(template: SummaryPrompt.defaultTemplate, language: .english)
        #expect(english.contains("\n## Summary\n") && english.contains("\n## Open Questions\n"))
        #expect(!english.contains("## Kurzfassung"))
        #expect(english.hasSuffix("„## Open Questions“."))

        let german = SummaryPrompt.instructions(template: SummaryPrompt.defaultTemplate, language: .german)
        #expect(german.contains("\n## Kurzfassung\n"))
        #expect(german.contains("mit genau diesen Abschnitten (Überschriften auf Deutsch):"))
        #expect(german.hasSuffix("ohne Einleitung oder Schlusssatz."))

        let czech = SummaryPrompt.instructions(template: SummaryPrompt.defaultTemplate, language: .czech)
        #expect(czech.contains("\n## Kurzfassung\n"))
        #expect(czech.hasSuffix("übersetze sie auf Tschechisch."))

        #expect(SummaryPrompt.instructions(template: "Fasse auf {language} zusammen.", language: .english)
                == "Fasse auf Englisch zusammen.")
        for language in AppLanguage.allCases {
            #expect(SummaryPrompt.headings(for: language).map(\.count) ?? 5 == 5)
        }
    }

    @Test("Bearbeiten setzt Text und Zeitpunkt, behält Herkunft; ohne Änderung bleibt alles gleich")
    func revise() {
        let created = Date(timeIntervalSince1970: 1_790_000_000)
        let edited = created.addingTimeInterval(60)
        let original = MeetingSummary(markdown: "## Kurzfassung\nAlt.", createdAt: created, model: "sonnet", language: .german)

        let revised = original.revised(markdown: "  ## Kurzfassung\nNeu.\n\n", at: edited)
        #expect(revised.markdown == "## Kurzfassung\nNeu.")
        #expect(revised.editedAt == edited)
        #expect(revised.isEdited)
        #expect(revised.createdAt == created && revised.model == "sonnet" && revised.language == .german)

        #expect(original.revised(markdown: "## Kurzfassung\nAlt.\n", at: edited) == original)
        #expect(!original.isEdited)
    }

    @Test("Ältere Zusammenfassungen ohne Sprache/Bearbeitung bleiben lesbar, nil-Felder werden nicht geschrieben")
    func codingIsBackwardCompatible() throws {
        let legacy = Data(#"{"markdown":"Kurz.","createdAt":"2026-09-21T10:00:00Z","model":"sonnet"}"#.utf8)
        let decoded = try JSONCoding.decoder().decode(MeetingSummary.self, from: legacy)
        #expect(decoded.language == nil && decoded.editedAt == nil)

        let encoded = try #require(String(data: try JSONCoding.encoder().encode(decoded), encoding: .utf8))
        #expect(!encoded.contains("language") && !encoded.contains("editedAt"))

        let full = decoded.revised(markdown: "Neu.", at: Date(timeIntervalSince1970: 1_790_000_000))
        var withLanguage = full
        withLanguage.language = .english
        let roundTrip = try JSONCoding.decoder().decode(MeetingSummary.self, from: try JSONCoding.encoder().encode(withLanguage))
        #expect(roundTrip == withLanguage)
    }

    @Test("Einstellung: fehlender oder ungültiger Wert heißt „wie das Meeting“")
    func sharedSettingsReadSummaryLanguage() throws {
        let name = "lunacore.test.summary.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }

        #expect(SharedSettings.read(from: defaults).summaryLanguage == .meeting)
        defaults.set("en", forKey: SharedSettings.Key.summaryLanguage)
        #expect(SharedSettings.read(from: defaults).summaryLanguage == .fixed(.english))
        defaults.set("unsinn", forKey: SharedSettings.Key.summaryLanguage)
        #expect(SharedSettings.read(from: defaults).summaryLanguage == .meeting)
    }
}
