import Foundation
import Testing
@testable import LunaCore

@Suite("Einstellungen")
struct SharedSettingsTests {
    @Test("Übernimmt die alte Domäne einmalig, ohne vorhandene Werte zu überschreiben")
    func migratesLegacyDomain() throws {
        let suffix = UUID().uuidString
        let legacyName = "lunacore.test.legacy.\(suffix)"
        let currentName = "lunacore.test.current.\(suffix)"
        let current = try #require(UserDefaults(suiteName: currentName))
        defer {
            current.removePersistentDomain(forName: legacyName)
            current.removePersistentDomain(forName: currentName)
        }
        current.setPersistentDomain([SharedSettings.Key.meetingFolder: "/alt", SharedSettings.Key.mcpAccessEnabled: true],
                                    forName: legacyName)
        current.set("/neu", forKey: SharedSettings.Key.meetingFolder)

        #expect(SharedSettings.migrateLegacyDomain(into: current, legacyDomain: legacyName) == 1)
        #expect(current.string(forKey: SharedSettings.Key.meetingFolder) == "/neu")
        #expect(current.bool(forKey: SharedSettings.Key.mcpAccessEnabled))

        current.removeObject(forKey: SharedSettings.Key.mcpAccessEnabled)
        #expect(SharedSettings.migrateLegacyDomain(into: current, legacyDomain: legacyName) == 0)
        #expect(current.object(forKey: SharedSettings.Key.mcpAccessEnabled) == nil)
    }
}
