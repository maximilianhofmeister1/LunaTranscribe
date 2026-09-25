import Foundation
import UserNotifications

/// macOS-Mitteilungen für Meetings, die im Hintergrund fertig (oder nicht fertig) wurden – statt ein
/// Fenster zu öffnen, das mitten in der Arbeit oder im nächsten Meeting stört. Ein Klick öffnet das Meeting.
///
/// Die Erlaubnis wird erst beim ersten beendeten Meeting erfragt, also dann, wenn der Nutzer versteht wofür.
@MainActor
final class MeetingNotifier: NSObject, UNUserNotificationCenterDelegate {
    private static let meetingIDKey = "meetingID"

    /// Wird mit der Meeting-ID aufgerufen, wenn der Nutzer auf eine Fertig-Mitteilung klickt.
    var onOpenMeeting: ((UUID) -> Void)?

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
        let idString = response.notification.request.content.userInfo[Self.meetingIDKey] as? String
        Task { @MainActor in
            if let idString, let id = UUID(uuidString: idString) { self.onOpenMeeting?(id) }
            completionHandler()
        }
    }
}
