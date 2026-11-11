import LunaCore
import SwiftUI

/// Tab „Zusammenfassung“ eines Meetings: Status (läuft, Fehler), die Zusammenfassung selbst und ihr
/// Bearbeiten. Der Entwurf liegt beim Aufrufer (`draft`, nil = nicht im Bearbeiten), damit ein
/// Tab-Wechsel ihn nicht verwirft und „Neu zusammenfassen“ währenddessen gesperrt werden kann.
struct MeetingSummaryTab: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var document: MeetingDocument
    @Binding var draft: String?

    private var transcript: MeetingTranscript { document.transcript }
    private var isSummarizing: Bool { state.summarizer.isRunning(transcript.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let error = state.summarizer.errors[transcript.id] {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(LunaFont.caption(12))
                    .foregroundStyle(LunaColor.rose)
                    .fixedSize(horizontal: false, vertical: true)
                    .lunaCard()
            }
            if isSummarizing {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text("Claude fasst das Meeting zusammen … das dauert je nach Länge 10–60 Sekunden.")
                        .font(LunaFont.text(13)).foregroundStyle(LunaColor.inkMuted)
                }
                .lunaCard()
            }
            if let summary = transcript.summary {
                if draft != nil {
                    SummaryEditor(text: draftText, onSave: save, onCancel: { draft = nil })
                } else {
                    summaryCard(summary)
                }
            } else if !isSummarizing {
                emptyState
            }
        }
    }

    private func summaryCard(_ summary: MeetingSummary) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(Self.origin(of: summary))
                    .font(LunaFont.caption(11.5))
                    .foregroundStyle(LunaColor.inkMuted)
                Spacer()
                Button {
                    draft = summary.markdown
                } label: {
                    Label("Bearbeiten", systemImage: "pencil")
                }
                .buttonStyle(.lunaQuiet)
                .keyboardShortcut("e", modifiers: .command)
                .disabled(isSummarizing)
                .help("Zusammenfassung umschreiben (⌘E) – wird im Meeting und in summary.md gespeichert")
            }
            MarkdownView(markdown: summary.markdown)
                .lunaCard(padding: 20)
        }
    }

    private var emptyState: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionTitle(title: "Noch keine Zusammenfassung", systemImage: "sparkles")
            Hint("„Zusammenfassen“ schickt das Transkript über die Claude CLI an Claude und legt Kurzfassung, Themen, Entscheidungen, Aufgaben und offene Fragen hier ab. Die Sprache wählst du daneben, Standard und Anweisung unter Einstellungen → Meetings.")
            Button("Einstellungen öffnen…") { state.openSettings(.meetings) }.buttonStyle(.lunaQuiet)
        }
        .lunaCard()
    }

    /// Nicht-optionale Sicht auf den Entwurf für den Editor. Bewusst nicht `Binding($draft)`: Dessen erzwungenes
    /// Entpacken stürzt ab, wenn SwiftUI den Editor nach „Abbrechen“/„Sichern“ (Entwurf = nil) beim Abbau noch
    /// einmal liest. Geschrieben wird nur, solange bearbeitet wird – ein später Schreibzugriff des Editors beim
    /// Abbau öffnet ihn also nicht wieder.
    private var draftText: Binding<String> {
        Binding(get: { draft ?? "" },
                set: { if draft != nil { draft = $0 } })
    }

    private func save() {
        if let draft { document.reviseSummary(to: draft) }
        draft = nil
    }

    /// „Erstellt 21.09.2026, 10:00 mit Claude (sonnet) · Englisch · bearbeitet 21.09.2026, 10:05“
    static func origin(of summary: MeetingSummary) -> String {
        var parts = ["Erstellt \(summary.createdAt.lunaDateTime) mit Claude\(summary.model.map { " (\($0))" } ?? "")"]
        if let language = summary.language { parts.append(language.displayName) }
        if let editedAt = summary.editedAt { parts.append("bearbeitet \(editedAt.lunaDateTime)") }
        return parts.joined(separator: " · ")
    }
}

/// Markdown-Quelltext der Zusammenfassung bearbeiten. Feste Schriftbreite, weil die Einrückung die
/// Gliederung bestimmt (`MarkdownBlock`). ⌘S sichert, Esc bricht ab (⌘E öffnet ihn).
struct SummaryEditor: View {
    @Binding var text: String
    let onSave: () -> Void
    let onCancel: () -> Void

    private var canSave: Bool { !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionTitle(title: "Zusammenfassung bearbeiten", systemImage: "pencil")
            TextEditor(text: $text)
                .font(LunaFont.mono(12.5))
                .scrollContentBackground(.hidden)
                .padding(8)
                .frame(minHeight: 380)
                .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(LunaColor.stroke))
            Hint("Markdown: „## “ für Überschriften, „- “ für Punkte, zwei Leerzeichen davor rücken ein, „- [ ] “ für Aufgaben, **fett** für Hervorhebungen.")
            HStack(spacing: 8) {
                Button("Sichern", action: onSave)
                    .buttonStyle(.lunaPrimary)
                    .keyboardShortcut("s", modifiers: .command)
                    .disabled(!canSave)
                Button("Abbrechen", action: onCancel)
                    .buttonStyle(.lunaQuiet)
                    .keyboardShortcut(.cancelAction)
            }
        }
        .lunaCard()
    }
}

/// Kleines Menü für die Sprache der nächsten Zusammenfassung: oben die Sprache des Meetings,
/// darunter alle anderen alphabetisch.
struct SummaryLanguageMenu: View {
    @Binding var selection: AppLanguage
    let meetingLanguage: AppLanguage

    var body: some View {
        Menu {
            Picker("Sprache der Zusammenfassung", selection: $selection) {
                Text("\(meetingLanguage.displayName) (Sprache des Meetings)").tag(meetingLanguage)
                Divider()
                ForEach(AppLanguage.sortedByDisplayName.filter { $0 != meetingLanguage }) { language in
                    Text(language.displayName).tag(language)
                }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            Label(selection.displayName, systemImage: "globe")
        }
        .menuStyle(.button)
        .buttonStyle(.lunaSecondary)
        .fixedSize()
        .help("Sprache der Zusammenfassung – Standard unter Einstellungen → Meetings")
    }
}
