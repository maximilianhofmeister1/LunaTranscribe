import SwiftUI

/// Die letzten Diktate (nur im Arbeitsspeicher). Kopieren, Rohtext ansehen, korrigieren –
/// Korrekturen werden zu Wörterbuch-Vorschlägen.
struct HistoryPage: View {
    @EnvironmentObject var state: AppState
    @State private var learnedHint: String?

    var body: some View {
        LunaPage(title: "Verlauf", subtitle: "Die letzten \(DictationController.historyLimit) Diktate – nur im Arbeitsspeicher, nie auf der Platte.") {
            HStack {
                Button { state.dictation.clearHistory() } label: { Label("Verlauf leeren", systemImage: "trash") }
                    .buttonStyle(.lunaDestructive)
                    .disabled(state.dictation.history.isEmpty)
            }
        } content: {
            if let learnedHint {
                Label(learnedHint, systemImage: "character.book.closed.fill")
                    .font(LunaFont.text(13, weight: .medium))
                    .foregroundStyle(LunaColor.honey)
                    .onTapGesture { state.openMainWindow(.dictionary) }
            }
            if !state.settings.keepHistory {
                Hint("Der Verlauf ist ausgeschaltet (Einstellungen → Datenschutz).").lunaCard()
            } else if state.dictation.history.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    SectionTitle(title: "Noch still hier", systemImage: "moon.zzz")
                    Hint("Diktiere etwas – es erscheint hier, bis du die App beendest. Ein falsch erkanntes Wort korrigierst du mit dem Stift; daraus wird ein Wörterbuch-Vorschlag.")
                }
                .lunaCard()
            } else {
                ForEach(state.dictation.history) { transcript in
                    HistoryRow(transcript: transcript) { corrected in
                        correct(transcript, to: corrected)
                    }
                }
            }
        }
    }

    private func correct(_ transcript: Transcript, to corrected: String) {
        let text = corrected.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, text != transcript.text else { return }
        state.dictation.updateHistoryText(id: transcript.id, to: text)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        let learned = state.learner.learn(before: transcript.text, after: text)
        learnedHint = learned == 0 ? "Korrigierter Text kopiert."
            : "Korrigierter Text kopiert · \(VocabularySuggestion.countLabel(learned)) – ansehen"
    }
}

private struct HistoryRow: View {
    let transcript: Transcript
    let onCorrect: (String) -> Void
    @State private var editing = false
    @State private var draft = ""
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(transcript.date.lunaTime)
                Text("·")
                Text(transcript.language.displayName)
                Text("·")
                Text("\(transcript.wordCount) Wörter in \(String(format: "%.1f", transcript.processingSeconds)) s")
                if transcript.rawText != nil {
                    Chip(text: "Nachbearbeitet", systemImage: "sparkles")
                }
                Spacer()
                if !editing {
                    Button { draft = transcript.text; editing = true } label: { Image(systemName: "pencil") }
                        .buttonStyle(.lunaQuiet)
                        .help("Korrigieren – das Wörterbuch lernt daraus")
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(transcript.text, forType: .string)
                        copied = true
                    } label: {
                        Image(systemName: copied ? "checkmark" : "doc.on.doc")
                    }
                    .buttonStyle(.lunaQuiet)
                    .help("In die Zwischenablage kopieren")
                }
            }
            .font(LunaFont.caption(11.5))
            .foregroundStyle(LunaColor.inkMuted)

            if editing {
                TextEditor(text: $draft)
                    .font(LunaFont.text(14))
                    .scrollContentBackground(.hidden)
                    .padding(6)
                    .frame(minHeight: 70)
                    .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 8))
                HStack {
                    Button("Übernehmen & kopieren") { onCorrect(draft); editing = false }.buttonStyle(.lunaPrimary)
                    Button("Abbrechen") { editing = false }.buttonStyle(.lunaQuiet)
                }
            } else {
                Text(transcript.text).font(LunaFont.text(14)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                if let raw = transcript.rawText {
                    DisclosureGroup("Rohtext vor der Nachbearbeitung") {
                        Text(raw).font(LunaFont.caption(12)).foregroundStyle(LunaColor.inkMuted).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .font(LunaFont.caption(11.5))
                    .foregroundStyle(LunaColor.inkMuted)
                }
            }
        }
        .lunaCard()
    }
}
