import SwiftUI

/// Meetings in der Hintergrund-Verarbeitung: Fortschritt, Wartende, Fehlgeschlagene mit
/// „Erneut versuchen“ / „Verwerfen“. Genutzt im Menü-Popover und auf der Meetings-Seite.
struct MeetingQueueView: View {
    @ObservedObject var queue: MeetingProcessingQueue
    /// Kompakte Darstellung fürs Popover.
    var compact = false
    @State private var confirmDiscard: MeetingJob?

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 8 : 10) {
            ForEach(queue.items) { item in
                row(item)
            }
        }
        .confirmationDialog("Aufnahme verwerfen?", isPresented: Binding(get: { confirmDiscard != nil }, set: { if !$0 { confirmDiscard = nil } }),
                            presenting: confirmDiscard) { job in
            Button("Verwerfen", role: .destructive) { queue.discard(job.id) }
        } message: { job in
            Text("„\(job.title)“ (\(MeetingTranscript.clock(job.durationSeconds))) wird endgültig gelöscht und nicht transkribiert.")
        }
    }

    @ViewBuilder
    private func row(_ item: MeetingProcessingQueue.Item) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                icon(item.state)
                Text(item.job.title)
                    .font(LunaFont.text(compact ? 12 : 13, weight: .medium))
                    .lineLimit(1)
                Spacer(minLength: 4)
                Text(MeetingTranscript.clock(item.job.durationSeconds))
                    .font(LunaFont.caption(11))
                    .foregroundStyle(LunaColor.inkMuted)
                    .monospacedDigit()
            }
            switch item.state {
            case .processing(let step, let fraction):
                ProgressView(value: fraction).tint(LunaColor.lavender)
                HStack {
                    Text(step).font(LunaFont.caption(11)).foregroundStyle(LunaColor.inkMuted)
                    Spacer()
                    Button("Abbrechen") { confirmDiscard = item.job }.buttonStyle(.lunaQuiet).font(LunaFont.caption(11))
                }
            case .waiting:
                HStack {
                    Text("Wartet auf Verarbeitung").font(LunaFont.caption(11)).foregroundStyle(LunaColor.inkMuted)
                    Spacer()
                    Button("Verwerfen") { confirmDiscard = item.job }.buttonStyle(.lunaQuiet).font(LunaFont.caption(11))
                }
            case .failed(let message):
                Text(message)
                    .font(LunaFont.caption(11))
                    .foregroundStyle(LunaColor.rose)
                    .lineLimit(compact ? 2 : 4)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 4) {
                    Button("Erneut versuchen") { queue.retry(item.id) }.buttonStyle(.lunaSecondary)
                    Button("Verwerfen") { confirmDiscard = item.job }.buttonStyle(.lunaQuiet)
                }
                .font(LunaFont.caption(11))
            }
        }
    }

    @ViewBuilder
    private func icon(_ state: MeetingProcessingQueue.State) -> some View {
        switch state {
        case .processing: ProgressView().controlSize(.mini)
        case .waiting: Image(systemName: "hourglass").foregroundStyle(LunaColor.inkMuted)
        case .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(LunaColor.rose)
        }
    }
}
