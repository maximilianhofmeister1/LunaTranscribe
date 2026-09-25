import SwiftUI

/// Wurzel des Hauptfensters: Seitenleiste + aktuelle Seite.
struct MainView: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var router: MainRouter

    var body: some View {
        NavigationSplitView {
            Sidebar(router: router)
                .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 260)
        } detail: {
            page
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(LunaColor.background)
        }
        .tint(LunaColor.lavender)
        .font(LunaFont.text())
        .foregroundStyle(LunaColor.ink)
    }

    @ViewBuilder
    private var page: some View {
        switch router.page {
        case .overview: OverviewPage()
        case .meetings: MeetingsPage(router: router)
        case .dictionary: DictionaryPage()
        case .history: HistoryPage()
        case .settings: SettingsPage(router: router)
        }
    }
}

private struct Sidebar: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var router: MainRouter

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 34, height: 34)
                (Text("Luna") + Text("Transcribe").foregroundStyle(LunaColor.lavender))
                    .font(LunaFont.display(20))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
            }
            .padding(.horizontal, 16)
            .padding(.top, 8)
            .padding(.bottom, 18)

            ForEach(MainPage.allCases) { page in
                SidebarItem(page: page, isSelected: router.page == page, badge: badge(for: page)) {
                    router.page = page
                }
            }

            Spacer()
            statusFooter
        }
        .padding(.top, 28)   // unter den Ampel-Knöpfen
        .frame(maxHeight: .infinity, alignment: .top)
        .background(LunaColor.surfaceMuted)
    }

    private func badge(for page: MainPage) -> Int {
        switch page {
        case .dictionary: return state.settings.vocabularySuggestions.count
        default: return 0
        }
    }

    private var statusFooter: some View {
        VStack(alignment: .leading, spacing: 6) {
            LoadStatusView(status: state.models.loadStatus)
            if state.meeting.isRecording {
                Label("Meeting läuft · \(MeetingTranscript.clock(state.meeting.elapsed))", systemImage: "record.circle")
                    .font(LunaFont.caption(12, weight: .medium))
                    .foregroundStyle(LunaColor.honey)
            }
            if state.meetingQueue.pendingCount > 0 {
                Label(state.meetingQueue.pendingCount == 1 ? "1 Meeting in Verarbeitung"
                                                           : "\(state.meetingQueue.pendingCount) Meetings in Verarbeitung",
                      systemImage: "waveform.badge.magnifyingglass")
                    .font(LunaFont.caption(12, weight: .medium))
                    .foregroundStyle(LunaColor.lavender)
            }
            Text("Lokal auf diesem Mac · Open Source")
                .font(LunaFont.caption(10.5))
                .foregroundStyle(LunaColor.inkMuted.opacity(0.8))
        }
        .padding(16)
    }
}

private struct SidebarItem: View {
    let page: MainPage
    let isSelected: Bool
    let badge: Int
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: page.systemImage)
                    .frame(width: 20)
                    .foregroundStyle(isSelected ? LunaColor.lavender : LunaColor.inkMuted)
                Text(page.title).font(LunaFont.text(13.5, weight: isSelected ? .semibold : .regular))
                Spacer()
                if badge > 0 {
                    Text("\(badge)")
                        .font(LunaFont.caption(10.5, weight: .semibold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 1)
                        .foregroundStyle(LunaColor.onAccent)
                        .background(LunaColor.honey, in: Capsule())
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: LunaMetrics.smallCornerRadius, style: .continuous)
                    .fill(isSelected ? LunaColor.surface : (hovering ? LunaColor.surface.opacity(0.5) : .clear))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .foregroundStyle(LunaColor.ink)
        .padding(.horizontal, 8)
        .onHover { hovering = $0 }
    }
}
