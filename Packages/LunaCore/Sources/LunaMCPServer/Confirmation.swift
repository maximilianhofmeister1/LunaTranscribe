import Foundation
import LunaCore

/// Kurzbeschreibung eines Meetings für Bestätigungsdialoge.
public struct MeetingDescription: Equatable, Sendable {
    public let id: UUID
    public let title: String
    public let date: Date
    public let durationSeconds: TimeInterval
    public let speakers: [String]

    init(_ meeting: MeetingTranscript) {
        id = meeting.id
        title = meeting.title
        date = meeting.date
        durationSeconds = meeting.durationSeconds
        speakers = meeting.speakerIDs.map(meeting.displayName(for:))
    }

    /// „„Planung Q4“ – 25.09.2026, 14:03 · 47 min · Anna, Sprecher 2“
    public var line: String {
        let minutes = max(1, Int((durationSeconds / 60).rounded()))
        let people = speakers.isEmpty ? "" : " · " + speakers.joined(separator: ", ")
        return "„\(title)“ – \(MeetingTranscript.Formatters.headerDate.string(from: date)) · \(minutes) min\(people)"
    }
}

/// Was der Nutzer bestätigen soll. Texte entstehen hier (testbar, deutsch); der Dialog zeigt sie nur an.
public enum ConfirmationRequest: Equatable, Sendable {
    case delete([MeetingDescription])
    case renameMeeting(MeetingDescription, newTitle: String)
    case renameSpeaker(MeetingDescription, speaker: String, newName: String)

    public var title: String {
        switch self {
        case .delete(let meetings): return meetings.count == 1 ? "Meeting löschen?" : "\(meetings.count) Meetings löschen?"
        case .renameMeeting: return "Meeting umbenennen?"
        case .renameSpeaker: return "Sprecher benennen?"
        }
    }

    public func message(client: String) -> String {
        let who = "Claude (\(client))"
        switch self {
        case .delete(let meetings):
            let list = meetings.map { "• " + $0.line }.joined(separator: "\n")
            let object = meetings.count == 1 ? "dieses Meeting" : "diese Meetings"
            return "\(who) möchte \(object) in den Papierkorb verschieben:\n\n\(list)\n\nDas Transkript, die Zusammenfassung und eine behaltene Aufnahme wandern mit. Im Finder aus dem Papierkorb wiederherstellbar."
        case .renameMeeting(let meeting, let newTitle):
            return "\(who) möchte das Meeting\n\(meeting.line)\numbenennen in „\(newTitle)“."
        case .renameSpeaker(let meeting, let speaker, let newName):
            let target = newName.isEmpty ? "auf den ursprünglichen Namen zurücksetzen" : "„\(newName)“ nennen"
            return "\(who) möchte in\n\(meeting.line)\n„\(speaker)“ \(target)."
        }
    }

    public var confirmButton: String {
        switch self {
        case .delete: return "In den Papierkorb"
        case .renameMeeting: return "Umbenennen"
        case .renameSpeaker: return "Benennen"
        }
    }

    public var isDestructive: Bool {
        if case .delete = self { return true }
        return false
    }
}

public enum ConfirmationOutcome: Equatable, Sendable {
    case approved
    case declined
    case timedOut
    /// Kein Dialog möglich (z. B. keine grafische Sitzung) – dann passiert nichts.
    case unavailable(String)
}

/// Fragt den Nutzer – außerhalb der Reichweite des Modells. Die macOS-Variante zeigt einen nativen Dialog.
public protocol UserConfirmer: Sendable {
    func confirm(_ request: ConfirmationRequest, client: String) async -> ConfirmationOutcome
}

/// Ohne Dialog (Linux, Tests ohne Ersatz): lehnt ab. Im Zweifel wird nichts gelöscht.
public struct UnavailableConfirmer: UserConfirmer {
    public init() {}
    public func confirm(_ request: ConfirmationRequest, client: String) async -> ConfirmationOutcome {
        .unavailable("Auf diesem System gibt es keinen Bestätigungsdialog.")
    }
}

/// Bremse gegen Dialog-Fluten (z. B. eine Prompt-Injection, die Löschen in Schleife auslöst):
/// immer nur ein offener Dialog, höchstens `maxDeleteDialogs` Lösch-Dialoge im `window`, und ein abgelehntes
/// Meeting wird für `window` nicht erneut angefragt.
actor ConfirmationGuard {
    private let window: TimeInterval
    private let maxDeleteDialogs: Int
    private var dialogOpen = false
    private var deleteDialogs: [Date] = []
    private var declined: [UUID: Date] = [:]

    init(window: TimeInterval = 600, maxDeleteDialogs: Int = 3) {
        self.window = window
        self.maxDeleteDialogs = maxDeleteDialogs
    }

    func begin(deleting ids: [UUID]?, now: Date) throws(ToolError) {
        guard !dialogOpen else {
            throw ToolError("Es ist bereits ein Bestätigungsdialog offen. Warte auf die Antwort des Nutzers.")
        }
        if let ids {
            deleteDialogs.removeAll { now.timeIntervalSince($0) > window }
            declined = declined.filter { now.timeIntervalSince($0.value) <= window }
            if ids.contains(where: { declined[$0] != nil }) {
                throw ToolError("Der Nutzer hat das Löschen dieses Meetings gerade abgelehnt. Nicht erneut anfragen, außer der Nutzer bittet ausdrücklich darum – dann in einigen Minuten.")
            }
            guard deleteDialogs.count < maxDeleteDialogs else {
                throw ToolError("Zu viele Lösch-Anfragen in kurzer Zeit. Bitte den Nutzer, Meetings in der App zu löschen, oder versuche es in einigen Minuten erneut.")
            }
            deleteDialogs.append(now)
        }
        dialogOpen = true
    }

    func end(declinedIDs: [UUID] = [], now: Date) {
        dialogOpen = false
        for id in declinedIDs { declined[id] = now }
    }
}
