import Foundation
import LunaCore

/// Abgeschirmte Umgebung für Selbsttests, die Einstellungen, Bibliothek oder Warteschlange verändern
/// (`--ui-snapshot`, `--queue-selftest`). Diese Tests laufen über den echten Code der App – würden sie dabei
/// die echten Einstellungen und Ordner benutzen, bliebe nach einem Absturz oder Abbruch (Timeout, `kill`,
/// ein Test, der gerade einen Absturz nachweisen soll) alles verstellt: `defer` läuft dann nicht mehr. So
/// geschehen in 1.2-dev: Meeting-Ordner auf einen Temp-Ordner verbogen, Test-Aufträge in der echten Warteschlange.
///
/// Deshalb arbeitet die App im Sandbox-Modus nie auf echten Daten:
/// - Einstellungen: eigene Defaults-Suite, bei jedem Start frisch aus den echten kopiert (Wörterbuch, Prompts …
///   sehen in den Snapshots echt aus), Claude-Zugriff aus – sonst gliche der Suchindex die Test-Bibliothek ab.
/// - Warteschlange, Rettungsordner: Temp-Ordner; alte Aufnahmen aus 1.1.0 werden nicht übernommen.
/// Reste eines abgebrochenen Laufs liegen nur im Temp-Verzeichnis und in der eigenen Suite und stören nichts.
@MainActor
enum SelfTestSandbox {
    /// Muss feststehen, bevor irgendwer `SettingsStore.shared` anfasst.
    static let isActive = UISnapshotSelfTest.isRequested || QueueSelfTest.isRequested

    private static let suiteName = "\(SharedSettings.appDomain).selftest"

    /// Einstellungen der Sandbox: Kopie der echten, ohne Claude-Zugriff.
    static let defaults: UserDefaults = {
        guard let suite = UserDefaults(suiteName: suiteName) else { return .standard }
        var settings = UserDefaults.standard.persistentDomain(forName: SharedSettings.appDomain) ?? [:]
        settings[SharedSettings.Key.mcpAccessEnabled] = false
        suite.setPersistentDomain(settings, forName: suiteName)
        return suite
    }()

    /// Temp-Ordner für alles, was sonst unter Application Support läge.
    private static let root = FileManager.default.temporaryDirectory
        .appending(path: "luna-selftest-\(ProcessInfo.processInfo.processIdentifier)", directoryHint: .isDirectory)
    static var queueFolder: URL { root.appending(path: "Queue", directoryHint: .isDirectory) }
    static var unsavedFolder: URL { root.appending(path: "Unsaved", directoryHint: .isDirectory) }

    /// Aufräumen bei regulärem Ende; nach einem Absturz bleibt nur Unschädliches liegen.
    static func cleanUp() {
        guard isActive else { return }
        UserDefaults.standard.removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: root)
    }
}
