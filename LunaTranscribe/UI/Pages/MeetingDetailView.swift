import LunaCore
import SwiftUI

/// Ein Meeting: Titel, Aktionen (Zusammenfassen, Export, Finder, Löschen) und zwei Tabs –
/// Transkript (Sprecher benennen, Beiträge korrigieren) und Zusammenfassung.
struct MeetingDetailView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var document: MeetingDocument

    private enum Tab: String, CaseIterable, Identifiable {
        case transcript = "Transkript"
        case summary = "Zusammenfassung"
        var id: String { rawValue }
    }

    @State private var tab: Tab = .transcript
    @State private var showCloudNotice = false
    @State private var confirmDelete = false
    @State private var actionMessage: String?

    private var transcript: MeetingTranscript { document.transcript }
    private var isSummarizing: Bool { state.summarizer.isRunning(transcript.id) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                header
                actions
                Picker("", selection: $tab) {
                    ForEach(Tab.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(maxWidth: 320)

                switch tab {
                case .transcript: transcriptTab
                case .summary: summaryTab
                }
            }
            .padding(LunaMetrics.pagePadding)
            .padding(.top, 8)
            .frame(maxWidth: 860, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(LunaColor.background)
        .alert("Zusammenfassung mit Claude", isPresented: $showCloudNotice) {
            Button("Verstanden, zusammenfassen") {
                state.settings.summaryCloudNoticeAccepted = true
                startSummary()
            }
            Button("Abbrechen", role: .cancel) {}
        } message: {
            Text("Die Transkription bleibt lokal. Für die Zusammenfassung schickt LunaTranscribe den Text des Meetings über die Claude CLI an Anthropic – mit deinem Claude-Konto. Dieser Hinweis erscheint nur einmal.")
        }
        .confirmationDialog("„\(transcript.title)“ löschen?", isPresented: $confirmDelete) {
            Button("In den Papierkorb legen", role: .destructive) { delete() }
        } message: {
            Text("Der Meeting-Ordner (Transkript, Zusammenfassung, ggf. Aufnahme) wandert in den Papierkorb.")
        }
    }

    // MARK: Kopf

    private var header: some View {
        VStack(alignment: .leading, spacing: 8) {
            TextField("Titel", text: $document.transcript.title, axis: .vertical)
                .textFieldStyle(.plain)
                .font(LunaFont.display(28))
                .lineLimit(1...3)
            HStack(spacing: 8) {
                Chip(text: transcript.date.lunaLongDateTime, systemImage: "calendar", tint: LunaColor.inkMuted)
                Chip(text: MeetingTranscript.clock(transcript.durationSeconds), systemImage: "clock", tint: LunaColor.inkMuted)
                Chip(text: "\(transcript.wordCount) Wörter", systemImage: "text.alignleft", tint: LunaColor.inkMuted)
                Chip(text: "\(transcript.engine.shortLabel) · \(transcript.language.displayName)", systemImage: "cpu", tint: LunaColor.inkMuted)
                Toggle(isOn: confidentialBinding) {
                    Label("Vertraulich", systemImage: transcript.confidential ? "lock.fill" : "lock.open")
                }
                .toggleStyle(.button)
                .buttonStyle(.lunaQuiet)
                .help("Vertrauliche Meetings sieht Claude nicht (luna-mcp), sie landen nicht im Suchindex und werden nicht über Claude zusammengefasst.")
            }
            if let error = document.saveError {
                Label(error, systemImage: "exclamationmark.triangle").font(LunaFont.caption(12)).foregroundStyle(LunaColor.rose)
            }
        }
    }

    private var actions: some View {
        HStack(spacing: 8) {
            if isSummarizing {
                Button {
                    state.summarizer.cancel(transcript.id)
                } label: {
                    Label("Zusammenfassung abbrechen", systemImage: "stop.circle")
                }
                .buttonStyle(.lunaSecondary)
                ProgressView().controlSize(.small)
            } else {
                Button {
                    requestSummary()
                } label: {
                    Label(transcript.summary == nil ? "Zusammenfassen" : "Neu zusammenfassen", systemImage: "sparkles")
                }
                .buttonStyle(.lunaPrimary)
                .disabled(transcript.confidential)
                .help(transcript.confidential
                      ? "Vertrauliche Meetings werden nicht an Claude geschickt."
                      : "Fasst das Meeting über die Claude CLI zusammen (Text geht an Anthropic)")
            }

            Menu {
                ForEach(MeetingTranscript.ExportFormat.allCases) { format in
                    Button("Transkript als \(format.label)…") { export(format, includeSummary: false) }
                    if transcript.summary != nil {
                        Button("Transkript + Zusammenfassung als \(format.label)…") { export(format, includeSummary: true) }
                    }
                }
                Divider()
                Button("Als Markdown kopieren") {
                    MeetingExporter.copyToPasteboard(transcript, includeSummary: transcript.summary != nil)
                    actionMessage = "In die Zwischenablage kopiert."
                }
            } label: {
                Label("Exportieren", systemImage: "square.and.arrow.up")
            }
            .menuStyle(.button)
            .buttonStyle(.lunaSecondary)
            .fixedSize()

            Button { state.meetingStore.revealInFinder(transcript.id) } label: { Image(systemName: "folder") }
                .buttonStyle(.lunaSecondary)
                .help("Im Finder zeigen")
            Spacer()
            Button { confirmDelete = true } label: { Image(systemName: "trash") }
                .buttonStyle(.lunaDestructive)
                .help("Meeting löschen")
        }
        .overlay(alignment: .bottomLeading) {
            if let actionMessage {
                Text(actionMessage).font(LunaFont.caption(11.5)).foregroundStyle(LunaColor.sage).offset(y: 20)
            }
        }
    }

    // MARK: Transkript

    private var transcriptTab: some View {
        VStack(alignment: .leading, spacing: 14) {
            speakers
            if document.learnedSuggestions > 0 {
                Label("\(VocabularySuggestion.countLabel(document.learnedSuggestions)) aus deiner Korrektur – ansehen",
                      systemImage: "character.book.closed.fill")
                    .font(LunaFont.caption(12, weight: .medium))
                    .foregroundStyle(LunaColor.honey)
                    .onTapGesture { state.openMainWindow(.dictionary) }
            }
            VStack(alignment: .leading, spacing: 4) {
                ForEach(transcript.turns) { turn in
                    TurnRow(turn: turn, speaker: transcript.displayName(for: turn.speakerID),
                            color: speakerColor(turn.speakerID)) { newText in
                        document.correctTurn(turn.id, to: newText)
                    }
                }
            }
            .lunaCard(padding: 10)
        }
    }

    private var speakers: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle(title: "Sprecher", systemImage: "person.2")
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 230), alignment: .leading)], alignment: .leading, spacing: 8) {
                ForEach(transcript.speakerIDs, id: \.self) { id in
                    HStack(spacing: 8) {
                        Circle().fill(speakerColor(id)).frame(width: 10, height: 10)
                        Text(id).font(LunaFont.caption(12)).foregroundStyle(LunaColor.inkMuted).frame(width: 72, alignment: .leading)
                        TextField("Name", text: nameBinding(for: id))
                            .textFieldStyle(.roundedBorder)
                    }
                }
            }
        }
        .lunaCard()
    }

    // MARK: Zusammenfassung

    @ViewBuilder
    private var summaryTab: some View {
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
                VStack(alignment: .leading, spacing: 10) {
                    MarkdownView(markdown: summary.markdown)
                    Divider()
                    Text("Erstellt \(summary.createdAt.lunaDateTime) mit Claude\(summary.model.map { " (\($0))" } ?? "")")
                        .font(LunaFont.caption(11)).foregroundStyle(LunaColor.inkMuted)
                }
                .lunaCard(padding: 20)
            } else if !isSummarizing {
                VStack(alignment: .leading, spacing: 10) {
                    SectionTitle(title: "Noch keine Zusammenfassung", systemImage: "sparkles")
                    Hint("„Zusammenfassen“ schickt das Transkript über die Claude CLI an Claude und legt Kurzfassung, Themen, Entscheidungen, Aufgaben und offene Fragen hier ab. Die Anweisung passt du unter Einstellungen → Meetings an.")
                    Button("Anweisung anpassen…") { state.openSettings(.meetings) }.buttonStyle(.lunaQuiet)
                }
                .lunaCard()
            }
        }
    }

    // MARK: Aktionen

    private func requestSummary() {
        tab = .summary
        if state.settings.summaryCloudNoticeAccepted { startSummary() } else { showCloudNotice = true }
    }

    private func startSummary() {
        document.flush()   // aktuelle Namen/Korrekturen mitschicken
        state.summarizer.summarize(document.transcript)
    }

    private func export(_ format: MeetingTranscript.ExportFormat, includeSummary: Bool) {
        do {
            if let url = try MeetingExporter.saveWithPanel(transcript, format: format, includeSummary: includeSummary,
                                                           startIn: state.meetingStore.libraryFolder) {
                actionMessage = "Exportiert: \(url.lastPathComponent)"
            }
        } catch {
            actionMessage = "Export fehlgeschlagen: \(error.localizedDescription)"
        }
    }

    private func delete() {
        do {
            try state.meetingStore.delete(transcript.id)
            state.router.selectedMeetingID = state.meetingStore.entries.first?.id
        } catch {
            actionMessage = "Löschen fehlgeschlagen: \(error.localizedDescription)"
        }
    }

    /// Im JSON optional (ältere Meetings haben das Feld nicht); „aus“ wird als fehlendes Feld gespeichert.
    private var confidentialBinding: Binding<Bool> {
        Binding(
            get: { document.transcript.confidential },
            set: { document.transcript.isConfidential = $0 ? true : nil }
        )
    }

    private func nameBinding(for speakerID: String) -> Binding<String> {
        Binding(
            get: { document.transcript.speakerNames[speakerID] ?? "" },
            set: { document.transcript.speakerNames[speakerID] = $0 }
        )
    }

    /// Feste, freundliche Farbe je Sprecher (Reihenfolge des ersten Auftretens).
    private func speakerColor(_ id: String) -> Color {
        let palette = [LunaColor.lavender, LunaColor.honey, LunaColor.sage, LunaColor.rose, Color(hex: 0x6FA8DC), Color(hex: 0xC27BA0)]
        let index = transcript.speakerIDs.firstIndex(of: id) ?? 0
        return palette[index % palette.count]
    }
}

/// Ein Redebeitrag; per Stift-Knopf bearbeitbar. Speichern meldet den neuen Text zurück.
private struct TurnRow: View {
    let turn: MeetingTranscript.Turn
    let speaker: String
    let color: Color
    let onCorrect: (String) -> Void

    @State private var editing = false
    @State private var draft = ""
    @State private var hovering = false

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            RoundedRectangle(cornerRadius: 2).fill(color).frame(width: 3)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(speaker).font(LunaFont.text(12.5, weight: .semibold)).foregroundStyle(color)
                    Text(MeetingTranscript.clock(turn.start)).font(LunaFont.caption(11)).foregroundStyle(LunaColor.inkMuted)
                    Spacer()
                    if !editing && hovering {
                        Button { draft = turn.text; editing = true } label: { Image(systemName: "pencil") }
                            .buttonStyle(.lunaQuiet)
                            .help("Korrigieren – das Wörterbuch lernt daraus")
                    }
                }
                if editing {
                    TextEditor(text: $draft)
                        .font(LunaFont.text(13.5))
                        .scrollContentBackground(.hidden)
                        .padding(6)
                        .frame(minHeight: 60)
                        .background(LunaColor.surfaceMuted, in: RoundedRectangle(cornerRadius: 8))
                    HStack {
                        Button("Übernehmen") { onCorrect(draft); editing = false }.buttonStyle(.lunaPrimary)
                        Button("Abbrechen") { editing = false }.buttonStyle(.lunaQuiet)
                    }
                } else {
                    Text(turn.text).font(LunaFont.text(13.5)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(.vertical, 6)
        .padding(.horizontal, 6)
        .background(hovering && !editing ? LunaColor.surfaceMuted.opacity(0.6) : .clear, in: RoundedRectangle(cornerRadius: 8))
        .onHover { hovering = $0 }
    }
}
