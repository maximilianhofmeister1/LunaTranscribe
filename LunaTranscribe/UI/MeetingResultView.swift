import SwiftUI
import AppKit
import Combine

/// Ergebnisfenster nach einem Meeting: Sprecher umbenennen, Beiträge lesen, exportieren.
struct MeetingResultView: View {
    @ObservedObject var session: MeetingSession
    @ObservedObject var settings = SettingsStore.shared
    @State private var lastSavedURL: URL?
    @State private var saveError: String?

    var body: some View {
        if let transcript = session.transcript {
            VStack(alignment: .leading, spacing: 12) {
                header(transcript)
                speakerEditor(transcript)
                Divider()
                turnList(transcript)
                Divider()
                footer(transcript)
            }
            .padding(16)
            .frame(minWidth: 560, idealWidth: 720, minHeight: 420, idealHeight: 640)
        } else {
            Text("Kein Meeting-Transkript vorhanden.")
                .foregroundStyle(.secondary)
                .frame(minWidth: 400, minHeight: 200)
        }
    }

    // MARK: Abschnitte

    private func header(_ t: MeetingTranscript) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Meeting vom \(t.date.formatted(date: .long, time: .shortened))").font(.title3.bold())
            Text("Dauer \(MeetingTranscript.clock(t.durationSeconds)) · \(t.turns.count) Beiträge · \(t.engine.shortLabel) · \(t.language.displayName)")
                .font(.caption).foregroundStyle(.secondary)
            if let backup = session.backupURL {
                Text("Backup: \(backup.path)").font(.caption2).foregroundStyle(.tertiary).lineLimit(1).truncationMode(.middle)
            }
        }
    }

    private func speakerEditor(_ t: MeetingTranscript) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Sprecher").font(.caption.bold()).foregroundStyle(.secondary)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 220), alignment: .leading)], alignment: .leading, spacing: 6) {
                ForEach(t.speakerIDs, id: \.self) { id in
                    HStack(spacing: 6) {
                        Text(id).font(.callout).frame(width: 84, alignment: .leading)
                        TextField("Name", text: nameBinding(for: id))
                            .textFieldStyle(.roundedBorder)
                    }
                }
            }
        }
    }

    private func turnList(_ t: MeetingTranscript) -> some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                ForEach(t.turns) { turn in
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 8) {
                            Text(t.displayName(for: turn.speakerID)).font(.callout.bold())
                            Text(MeetingTranscript.clock(turn.start)).font(.caption).foregroundStyle(.secondary)
                        }
                        Text(turn.text).font(.body).textSelection(.enabled)
                    }
                }
            }
            .padding(.trailing, 8)
        }
    }

    private func footer(_ t: MeetingTranscript) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Button("Als Text sichern…") { save(t, as: .text) }
                Button("Als Markdown sichern…") { save(t, as: .markdown) }
                Button("Kopieren") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(t.render(as: .text), forType: .string)
                }
                Spacer()
                Button("Schließen") { NSApp.keyWindow?.close() }
                    .keyboardShortcut(.cancelAction)
            }
            if let url = lastSavedURL {
                Text("Gesichert: \(url.path)").font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            if let saveError {
                Text(saveError).font(.caption).foregroundStyle(.red)
            }
        }
    }

    // MARK: Aktionen

    private func nameBinding(for speakerID: String) -> Binding<String> {
        Binding(
            get: { session.transcript?.speakerNames[speakerID] ?? "" },
            set: { session.transcript?.speakerNames[speakerID] = $0 }
        )
    }

    private func save(_ t: MeetingTranscript, as format: MeetingTranscript.ExportFormat) {
        saveError = nil
        do {
            let folder = MeetingExporter.folder(from: settings.meetingExportFolder)
            if let url = try MeetingExporter.saveWithPanel(t, format: format, defaultFolder: folder) {
                lastSavedURL = url
            }
        } catch {
            saveError = "Sichern fehlgeschlagen: \(error.localizedDescription)"
        }
    }
}

/// Hält das Ergebnisfenster als normales `NSWindow` (nicht als SwiftUI-Scene): so lässt es sich aus
/// jedem Kontext öffnen – auch wenn das Menü-Popover gerade geschlossen ist.
@MainActor
final class MeetingResultWindowController {
    private var window: NSWindow?
    private var cancellable: AnyCancellable?

    func bind(to session: MeetingSession) {
        // Nur bei einem *neuen* Transkript öffnen – jede Umbenennung publiziert den Struct ebenfalls.
        cancellable = session.$transcript
            .map { $0?.date }
            .removeDuplicates()
            .dropFirst()
            .sink { [weak self] date in
                if date != nil { self?.show(session: session) }
            }
    }

    func show(session: MeetingSession) {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 640),
                             styleMask: [.titled, .closable, .resizable, .miniaturizable],
                             backing: .buffered, defer: false)
            w.title = "Meeting-Transkript"
            w.contentView = NSHostingView(rootView: MeetingResultView(session: session))
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
