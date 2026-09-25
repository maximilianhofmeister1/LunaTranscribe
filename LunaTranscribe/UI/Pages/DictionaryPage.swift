import SwiftUI

/// Eigenes Wörterbuch: gelernte Vorschläge oben, Einträge darunter.
struct DictionaryPage: View {
    @EnvironmentObject var state: AppState
    @ObservedObject private var settings = SettingsStore.shared
    @State private var query = ""

    var body: some View {
        LunaPage(title: "Wörterbuch", subtitle: "Namen und Fachbegriffe, die immer richtig geschrieben werden sollen.") {
            Toggle(isOn: $settings.vocabularyEnabled) {
                Text("Eigenes Wörterbuch verwenden (Diktat und Meeting)").font(LunaFont.text(13.5, weight: .medium))
            }
            .toggleStyle(.switch)
            .lunaCard()

            if settings.vocabularyEnabled {
                if !settings.vocabularySuggestions.isEmpty { suggestions }
                entries
            }
        }
    }

    // MARK: Vorschläge

    private var suggestions: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                SectionTitle(title: "Gelernt aus deinen Korrekturen", systemImage: "sparkles")
                Spacer()
                Chip(text: "\(settings.vocabularySuggestions.count) offen", tint: LunaColor.honey)
            }
            Hint("Wenn du im Verlauf oder in einem Meeting ein Wort korrigierst, schlägt LunaTranscribe hier einen Eintrag vor. Aktiv wird er erst, wenn du ihn übernimmst.")
            ForEach(settings.vocabularySuggestions) { suggestion in
                SuggestionRow(suggestion: suggestion,
                              onAccept: { state.learner.accept($0) },
                              onIgnore: { state.learner.ignore(suggestion) })
            }
        }
        .lunaCard()
    }

    // MARK: Einträge

    private var entries: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                SectionTitle(title: "Einträge", systemImage: "character.book.closed")
                Spacer()
                LoadStatusView(status: state.models.vocabularyStatus)
            }
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(LunaColor.inkMuted)
                TextField("Filtern", text: $query).textFieldStyle(.plain)
            }
            .padding(8)
            .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 8))

            HStack {
                Text("Richtige Schreibweise").frame(width: 200, alignment: .leading)
                Text("Wird erkannt als … (durch Kommas getrennt)")
            }
            .font(LunaFont.caption(11, weight: .semibold))
            .foregroundStyle(LunaColor.inkMuted)

            ForEach(filteredEntryBindings) { entry in
                let id = entry.wrappedValue.id
                VocabularyEntryRow(entry: entry) {
                    settings.vocabularyEntries.removeAll { $0.id == id }
                }
            }
            if settings.vocabularyEntries.isEmpty {
                Hint("Noch keine Begriffe. Beispiel: „weclapp“ – erkannt als „Weglap, Weg Lab“.")
            }
            Button {
                query = ""
                settings.vocabularyEntries.append(VocabularyEntry(term: ""))
            } label: {
                Label("Begriff hinzufügen", systemImage: "plus")
            }
            .buttonStyle(.lunaSecondary)

            LearnMore(text: "Zwei Stufen aus derselben Liste: Varianten werden immer ersetzt – unabhängig von Engine und Sprache, nur an Wortgrenzen. Mit Parakeet prüft zusätzlich ein kleines Zusatzmodell (Parakeet CTC 110M, einmalig ~100 MB) am Audio, ob ein Begriff gesagt wurde, und findet so auch unbekannte Fehlschreibungen. Das Zusatzmodell ist auf Englisch trainiert und wirkt am besten bei Namen und Fachbegriffen ab vier Buchstaben. Nach der Nachbearbeitung wird das Wörterbuch erneut angewendet, damit das Sprachmodell nichts zurückkorrigiert.")
        }
        .lunaCard()
    }

    private var filteredEntryBindings: [Binding<VocabularyEntry>] {
        let needle = query.trimmingCharacters(in: .whitespaces).lowercased()
        return $settings.vocabularyEntries.filter { entry in
            needle.isEmpty || entry.wrappedValue.term.isEmpty
                || entry.wrappedValue.term.lowercased().contains(needle)
                || entry.wrappedValue.aliases.contains { $0.lowercased().contains(needle) }
        }
    }
}

/// Ein Vorschlag mit editierbarem Begriff (falls die Korrektur selbst nicht ganz stimmte).
private struct SuggestionRow: View {
    let suggestion: VocabularySuggestion
    let onAccept: (VocabularySuggestion) -> Void
    let onIgnore: () -> Void
    @State private var term = ""

    var body: some View {
        HStack(spacing: 10) {
            Text(suggestion.variant)
                .strikethrough(color: LunaColor.rose)
                .foregroundStyle(LunaColor.inkMuted)
            Image(systemName: "arrow.right").foregroundStyle(LunaColor.lavender)
            TextField("Begriff", text: $term)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 200)
            if suggestion.count > 1 {
                Chip(text: "\(suggestion.count)×", tint: LunaColor.honey)
            }
            Spacer()
            Button("Übernehmen") {
                var accepted = suggestion
                accepted.term = term.trimmingCharacters(in: .whitespaces)
                onAccept(accepted)
            }
            .buttonStyle(.lunaPrimary)
            .disabled(term.trimmingCharacters(in: .whitespaces).isEmpty)
            Button("Ignorieren", action: onIgnore).buttonStyle(.lunaQuiet)
        }
        .font(LunaFont.text(13))
        .onAppear { term = suggestion.term }
    }
}

/// Eine Zeile im Wörterbuch: Begriff und Fehlerkennungen als kommagetrennter Text.
struct VocabularyEntryRow: View {
    @Binding var entry: VocabularyEntry
    let onDelete: () -> Void

    /// Eigener Textzustand, damit ein gerade getipptes „, “ nicht sofort wieder weggeparst wird.
    @State private var aliasText = ""

    var body: some View {
        HStack {
            TextField("Begriff", text: $entry.term, prompt: Text("Richtige Schreibweise"))
                .textFieldStyle(.roundedBorder)
                .frame(width: 200)
            TextField("Varianten", text: $aliasText, prompt: Text("Falsch erkannt als …"))
                .textFieldStyle(.roundedBorder)
                .onChange(of: aliasText) { _, text in
                    entry.aliases = text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
                }
            Button(action: onDelete) { Image(systemName: "minus.circle") }
                .buttonStyle(.borderless)
                .foregroundStyle(LunaColor.rose)
                .help("Begriff entfernen")
        }
        .onAppear { aliasText = entry.aliases.joined(separator: ", ") }
        .onChange(of: entry.aliases) { _, aliases in
            // Von außen ergänzt (übernommener Vorschlag)? Dann Anzeige nachziehen.
            let parsed = aliasText.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            if parsed != aliases { aliasText = aliases.joined(separator: ", ") }
        }
    }
}
