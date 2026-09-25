import SwiftUI

/// Meeting-Bibliothek: Liste (Suche, nach Monat gruppiert) links, gewähltes Meeting rechts.
struct MeetingsPage: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var router: MainRouter
    @State private var query = ""

    var body: some View {
        HStack(spacing: 0) {
            list
                .frame(width: 290)
                .background(LunaColor.background)
            Divider().overlay(LunaColor.stroke)
            detail
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .onAppear(perform: selectFirstIfNeeded)
        .onChange(of: state.meetingStore.entries) { _, _ in selectFirstIfNeeded() }
    }

    // MARK: Liste

    private var list: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Meetings").font(LunaFont.display(26)).padding(.top, 8)
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(LunaColor.inkMuted)
                TextField("Suchen in Titeln und Texten", text: $query)
                    .textFieldStyle(.plain)
                    .font(LunaFont.text(13))
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(LunaColor.surface, in: RoundedRectangle(cornerRadius: LunaMetrics.smallCornerRadius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: LunaMetrics.smallCornerRadius, style: .continuous).strokeBorder(LunaColor.stroke))

            if let issue = state.meetingStore.folderIssue {
                Label(issue, systemImage: "externaldrive.badge.exclamationmark")
                    .font(LunaFont.caption(11.5))
                    .foregroundStyle(LunaColor.honey)
                    .fixedSize(horizontal: false, vertical: true)
            }

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 4, pinnedViews: [.sectionHeaders]) {
                    ForEach(groupedEntries, id: \.month) { group in
                        Section {
                            ForEach(group.entries) { entry in
                                Button { router.selectedMeetingID = entry.id } label: {
                                    MeetingRow(entry: entry, isRunning: state.summarizer.isRunning(entry.id))
                                        .padding(.horizontal, 10)
                                        .padding(.vertical, 8)
                                        .background(
                                            RoundedRectangle(cornerRadius: LunaMetrics.smallCornerRadius, style: .continuous)
                                                .fill(router.selectedMeetingID == entry.id ? LunaColor.surface : .clear)
                                        )
                                        .contentShape(Rectangle())
                                }
                                .buttonStyle(.plain)
                            }
                        } header: {
                            Text(group.month)
                                .font(LunaFont.caption(11, weight: .semibold))
                                .textCase(.uppercase)
                                .foregroundStyle(LunaColor.inkMuted)
                                .padding(.top, 10)
                                .padding(.bottom, 2)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .background(LunaColor.background)
                        }
                    }
                }
            }
            .scrollIndicators(.never)

            HStack {
                Button {
                    NSWorkspace.shared.open(state.meetingStore.libraryFolder)
                } label: {
                    Label("Meeting-Ordner öffnen", systemImage: "folder")
                }
                .buttonStyle(.lunaQuiet)
                Spacer()
                Button { state.meetingStore.reload() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.lunaQuiet)
                    .help("Neu einlesen (z. B. nach Änderungen im Finder)")
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 28)
        .padding(.bottom, 10)
    }

    @ViewBuilder
    private var detail: some View {
        if let id = router.selectedMeetingID, state.meetingStore.entry(for: id) != nil {
            MeetingDetailLoader(meetingID: id)
                .id(id)
        } else {
            emptyState
        }
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Image(systemName: "moon.stars")
                .font(.system(size: 48))
                .foregroundStyle(LunaColor.lavender)
            Text(state.meetingStore.entries.isEmpty ? "Noch keine Meetings" : "Wähle ein Meeting")
                .font(LunaFont.display(22))
            Text("Meetings startest du über die Menüleiste. Nach dem Beenden erkennt LunaTranscribe die Sprecher, transkribiert lokal und legt alles hier ab.")
                .font(LunaFont.text(13))
                .foregroundStyle(LunaColor.inkMuted)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(LunaColor.background)
    }

    // MARK: Daten

    private struct MonthGroup {
        let month: String
        let entries: [MeetingStore.Entry]
    }

    private var groupedEntries: [MonthGroup] {
        let entries = state.meetingStore.search(query)
        var groups: [MonthGroup] = []
        for entry in entries {
            let month = Self.monthFormatter.string(from: entry.date)
            if groups.last?.month == month {
                groups[groups.count - 1] = MonthGroup(month: month, entries: groups[groups.count - 1].entries + [entry])
            } else {
                groups.append(MonthGroup(month: month, entries: [entry]))
            }
        }
        return groups
    }

    private func selectFirstIfNeeded() {
        let entries = state.meetingStore.entries
        if router.selectedMeetingID.flatMap(state.meetingStore.entry(for:)) == nil {
            router.selectedMeetingID = entries.first?.id
        }
    }

    private static let monthFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "de_DE")
        f.dateFormat = "LLLL yyyy"
        return f
    }()
}

/// Eine Zeile in Meeting-Listen (Bibliothek, Übersicht).
struct MeetingRow: View {
    let entry: MeetingStore.Entry
    var isRunning = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(entry.title)
                .font(LunaFont.text(13.5, weight: .semibold))
                .lineLimit(2)
                .foregroundStyle(LunaColor.ink)
            HStack(spacing: 6) {
                Text(entry.date.lunaDateTime)
                Text("·")
                Text(MeetingTranscript.clock(entry.durationSeconds))
                Text("·")
                Text("\(entry.speakerCount) Sprecher")
            }
            .font(LunaFont.caption(11.5))
            .foregroundStyle(LunaColor.inkMuted)
            HStack(spacing: 6) {
                if isRunning {
                    Chip(text: "Fasst zusammen…", systemImage: "cloud", tint: LunaColor.lavender)
                } else if entry.hasSummary {
                    Chip(text: "Zusammenfassung", systemImage: "sparkles", tint: LunaColor.lavender)
                }
                if entry.isUnsaved {
                    Chip(text: "Nur in der App gesichert", systemImage: "exclamationmark.triangle", tint: LunaColor.honey)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Lädt ein Meeting von der Platte und zeigt es an; Fehler (Datei im Finder gelöscht o. ä.) sichtbar.
private struct MeetingDetailLoader: View {
    @EnvironmentObject var state: AppState
    let meetingID: UUID
    @State private var document: MeetingDocument?
    @State private var error: String?

    var body: some View {
        Group {
            if let document {
                MeetingDetailView(document: document)
            } else if let error {
                Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(LunaColor.rose).padding()
            } else {
                ProgressView()
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(LunaColor.background)
        .task(id: meetingID) { load() }
        .onDisappear { document?.flush() }
    }

    private func load() {
        do {
            let transcript = try state.meetingStore.load(meetingID)
            document = MeetingDocument(transcript: transcript, store: state.meetingStore,
                                       learner: state.learner, summarizer: state.summarizer)
        } catch {
            self.error = error.localizedDescription
        }
    }
}
