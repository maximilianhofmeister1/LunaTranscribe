import Foundation
import UserNotifications

/// macOS-Mitteilungen für Meetings, die im Hintergrund fertig (oder nicht fertig) wurden – statt ein
/// Fenster zu öffnen, das mitten in der Arbeit oder im nächsten Meeting stört. Ein Klick öffnet das Meeting.
///
/// Die Erlaubnis wird erst beim ersten beendeten Meeting erfragt, also dann, wenn der Nutzer versteht wofür.
@MainActor
final class MeetingNotifier: NSObject, UNUserNotificationCenterDelegate {
    private nonisolated static let meetingIDKey = "meetingID"
    private nonisolated static let openMeetingsKey = "openMeetings"

    /// Wird mit der Meeting-ID aufgerufen, wenn der Nutzer auf eine Fertig-Mitteilung klickt.
    var onOpenMeeting: ((UUID) -> Void)?
    /// Klick auf die Mitteilung über gerettete Aufnahmen: Meetings-Seite öffnen.
    var onOpenMeetings: (() -> Void)?

    private var center: UNUserNotificationCenter { .current() }

    override init() {
        super.init()
        center.delegate = self
    }

    /// Erlaubnis anfragen (zeigt den Systemdialog nur beim allerersten Mal).
    func requestAuthorizationIfNeeded() {
        center.getNotificationSettings { settings in
            guard settings.authorizationStatus == .notDetermined else { return }
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, error in
                if let error { Log.meeting.error("notification permission: \(error.localizedDescription, privacy: .public)") }
                Log.meeting.notice("notification permission granted=\(granted)")
            }
        }
    }

    func meetingFinished(title: String, meetingID: UUID) {
        post(title: "Meeting fertig transkribiert", body: "„\(title)“ liegt in deiner Bibliothek. Klicken zum Öffnen.",
             userInfo: [Self.meetingIDKey: meetingID.uuidString])
    }

    func meetingFailed(title: String, message: String) {
        post(title: "Meeting konnte nicht verarbeitet werden",
             body: "„\(title)“: \(message) Die Aufnahme bleibt erhalten – im Menü erneut versuchen.", userInfo: [:])
    }

    /// Beim Start wurden unterbrochene Aufnahmen gerettet. Fragt die Erlaubnis bei Bedarf selbst an –
    /// die Mitteilung kommt direkt beim Start, vor dem ersten eingereihten Meeting.
    func recordingsRecovered(count: Int) {
        let body = count == 1
            ? "Ein Meeting lief noch, als LunaTranscribe beendet wurde. Die Aufnahme ist gesichert – klicken, um sie zu transkribieren oder zu verwerfen."
            : "\(count) Meetings liefen noch, als LunaTranscribe beendet wurde. Die Aufnahmen sind gesichert – klicken, um sie zu transkribieren oder zu verwerfen."
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            Task { @MainActor in
                self.post(title: count == 1 ? "Unterbrochene Meeting-Aufnahme gerettet" : "Unterbrochene Meeting-Aufnahmen gerettet",
                          body: body, userInfo: [Self.openMeetingsKey: "1"])
            }
        }
    }

    private func post(title: String, body: String, userInfo: [String: String]) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        content.userInfo = userInfo
        center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)) { error in
            if let error { Log.meeting.error("notification failed: \(error.localizedDescription, privacy: .public)") }
        }
    }

    // MARK: UNUserNotificationCenterDelegate

    /// Auch zeigen, wenn die App gerade aktiv ist (z. B. Hauptfenster offen).
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let userInfo = response.notification.request.content.userInfo
        let idString = userInfo[Self.meetingIDKey] as? String
        let openMeetings = userInfo[Self.openMeetingsKey] != nil
        Task { @MainActor in
            if let idString, let id = UUID(uuidString: idString) {
                self.onOpenMeeting?(id)
            } else if openMeetings {
                self.onOpenMeetings?()
            }
            completionHandler()
        }
    }
}
