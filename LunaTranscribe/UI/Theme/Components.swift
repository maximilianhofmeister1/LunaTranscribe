import SwiftUI

// Wiederverwendbare Bausteine im Mondnacht-Stil. Neue Views setzen sich daraus zusammen, statt
// Farben, Schriften und Abstände selbst zu wählen.

// MARK: Karte

struct LunaCardModifier: ViewModifier {
    var padding: CGFloat = LunaMetrics.cardPadding

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(LunaColor.surface, in: RoundedRectangle(cornerRadius: LunaMetrics.cornerRadius, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: LunaMetrics.cornerRadius, style: .continuous)
                .strokeBorder(LunaColor.stroke, lineWidth: 1))
            .shadow(color: .black.opacity(0.04), radius: 8, y: 2)
    }
}

extension View {
    func lunaCard(padding: CGFloat = LunaMetrics.cardPadding) -> some View { modifier(LunaCardModifier(padding: padding)) }
}

// MARK: Buttons

struct LunaButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, quiet, destructive }
    var kind: Kind = .secondary
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(LunaFont.text(13, weight: .medium))
            .padding(.horizontal, kind == .quiet ? 6 : 14)
            .padding(.vertical, 7)
            .foregroundStyle(foreground)
            .background(background, in: Capsule())
            .overlay(Capsule().strokeBorder(kind == .secondary ? LunaColor.stroke : .clear, lineWidth: 1))
            .opacity(isEnabled ? (configuration.isPressed ? 0.75 : 1) : 0.45)
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
            .contentShape(Capsule())
    }

    private var foreground: Color {
        switch kind {
        case .primary: return LunaColor.onAccent
        case .secondary: return LunaColor.ink
        case .quiet: return LunaColor.lavender
        case .destructive: return LunaColor.rose
        }
    }

    private var background: Color {
        switch kind {
        case .primary: return LunaColor.lavender
        case .secondary: return LunaColor.surface
        case .quiet: return .clear
        case .destructive: return LunaColor.rose.opacity(0.12)
        }
    }
}

extension ButtonStyle where Self == LunaButtonStyle {
    static var lunaPrimary: LunaButtonStyle { LunaButtonStyle(kind: .primary) }
    static var lunaSecondary: LunaButtonStyle { LunaButtonStyle(kind: .secondary) }
    static var lunaQuiet: LunaButtonStyle { LunaButtonStyle(kind: .quiet) }
    static var lunaDestructive: LunaButtonStyle { LunaButtonStyle(kind: .destructive) }
}

// MARK: Seitengerüst

/// Seite im Hauptfenster: verspielter Titel, Untertitel, scrollender Inhalt.
struct LunaPage<Content: View, Accessory: View>: View {
    let title: String
    var subtitle: String?
    @ViewBuilder var accessory: () -> Accessory
    @ViewBuilder var content: () -> Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(title).font(LunaFont.display(30)).foregroundStyle(LunaColor.ink)
                        if let subtitle {
                            Text(subtitle).font(LunaFont.text(13)).foregroundStyle(LunaColor.inkMuted)
                        }
                    }
                    Spacer(minLength: 12)
                    accessory()
                }
                content()
            }
            .padding(LunaMetrics.pagePadding)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollContentBackground(.hidden)
        .background(LunaColor.background)
    }
}

extension LunaPage where Accessory == EmptyView {
    init(title: String, subtitle: String? = nil, @ViewBuilder content: @escaping () -> Content) {
        self.init(title: title, subtitle: subtitle, accessory: { EmptyView() }, content: content)
    }
}

/// Überschrift einer Karte oder eines Abschnitts.
struct SectionTitle: View {
    let title: String
    var systemImage: String?

    var body: some View {
        HStack(spacing: 8) {
            if let systemImage {
                Image(systemName: systemImage).foregroundStyle(LunaColor.lavender)
            }
            Text(title).font(LunaFont.title(16)).foregroundStyle(LunaColor.ink)
        }
    }
}

/// Erklärtext, der erst auf Wunsch aufklappt – hält Einstellungsseiten ruhig.
struct LearnMore: View {
    let text: String
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Button {
                withAnimation(.easeInOut(duration: 0.18)) { expanded.toggle() }
            } label: {
                Label(expanded ? "Weniger" : "Mehr erfahren", systemImage: expanded ? "chevron.up" : "chevron.down")
                    .font(LunaFont.caption(weight: .medium))
            }
            .buttonStyle(.plain)
            .foregroundStyle(LunaColor.lavender)
            if expanded {
                Text(text)
                    .font(LunaFont.caption(12))
                    .foregroundStyle(LunaColor.inkMuted)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity)
            }
        }
    }
}

/// Kleiner Hinweistext unter Bedienelementen.
struct Hint: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(LunaFont.caption(12))
            .foregroundStyle(LunaColor.inkMuted)
            .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: Status

/// Einheitliche Darstellung eines Ladezustands (ASR-Modell, Sprachmodell, Wörterbuch).
struct LoadStatus: Equatable {
    enum Phase: Equatable { case idle, loading(Double?), ready, warning, failed }
    let phase: Phase
    let text: String
}

struct LoadStatusView: View {
    let status: LoadStatus
    var onRetry: (() -> Void)?

    var body: some View {
        HStack(spacing: 8) {
            switch status.phase {
            case .loading(let fraction):
                if let fraction {
                    ProgressView(value: fraction).frame(width: 90).tint(LunaColor.lavender)
                } else {
                    ProgressView().controlSize(.small)
                }
            case .ready:
                Image(systemName: "checkmark.circle.fill").foregroundStyle(LunaColor.sage)
            case .warning:
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(LunaColor.honey)
            case .failed:
                Image(systemName: "xmark.circle.fill").foregroundStyle(LunaColor.rose)
            case .idle:
                Image(systemName: "moon.zzz").foregroundStyle(LunaColor.inkMuted)
            }
            Text(status.text)
                .font(LunaFont.caption(12))
                .foregroundStyle(status.phase == .failed ? LunaColor.rose : LunaColor.inkMuted)
                .lineLimit(3)
                .fixedSize(horizontal: false, vertical: true)
            if status.phase == .failed, let onRetry {
                Button("Erneut versuchen", action: onRetry).buttonStyle(.lunaQuiet)
            }
        }
    }
}

/// Kleines farbiges Etikett („✨ Zusammenfassung“, „3 Sprecher“).
struct Chip: View {
    let text: String
    var systemImage: String?
    var tint: Color = LunaColor.lavender

    var body: some View {
        HStack(spacing: 4) {
            if let systemImage { Image(systemName: systemImage) }
            Text(text)
        }
        .font(LunaFont.caption(11, weight: .medium))
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .foregroundStyle(tint)
        .background(tint.opacity(0.13), in: Capsule())
    }
}

// MARK: Wellenform

/// Lebendige Wellenform aus dem Eingangspegel: Balken schwingen phasenversetzt, ihre Höhe folgt dem Pegel.
struct WaveformView: View {
    let level: Float
    var barCount = 9
    var color: Color = LunaColor.honey

    var body: some View {
        TimelineView(.animation) { context in
            let t = context.date.timeIntervalSinceReferenceDate
            HStack(alignment: .center, spacing: 3) {
                ForEach(0..<barCount, id: \.self) { i in
                    let wobble = (sin(t * 7 + Double(i) * 0.9) + 1) / 2
                    let height = 4 + CGFloat(min(max(level, 0), 1)) * 18 * (0.45 + 0.55 * wobble)
                    Capsule().fill(color).frame(width: 3, height: height)
                }
            }
            .frame(height: 22)
        }
    }
}
