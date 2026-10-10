import Foundation

/// Die Einstellungen, die luna-mcp aus der Einstellungsdomäne der App liest. Geschrieben werden sie nur
/// von der App (`SettingsStore`); die Schlüssel sind gespeicherte Daten und müssen mit denen dort
/// übereinstimmen – nie umbenennen.
public struct SharedSettings: Equatable, Sendable {
    /// Bundle-ID der App = Name ihrer UserDefaults-Domäne.
    public static let appDomain = "at.aljoma.lunatranscribe"
    /// Bundle-ID vor Version 1.1.0; ihre Einstellungen übernimmt `migrateLegacyDomain` einmalig.
    public static let legacyAppDomain = "com.maximhofmeister.LunaTranscribe"
    /// Merker in der neuen Domäne: Übernahme ist erledigt (auch wenn es nichts zu übernehmen gab).
    static let legacyMigrationKey = "migratedFromLegacyDomain"

    public enum Key {
        /// Historischer Name aus der Zeit, als der Ordner nur Backups/Exporte enthielt.
        public static let meetingFolder = "meetingExportFolder"
        public static let vocabularyEnabled = "vocabularyEnabled"
        public static let vocabularyEntries = "vocabularyEntries"
        public static let summaryPrompt = "summaryPrompt"
        public static let mcpAccessEnabled = "mcpAccessEnabled"
        public static let mcpWriteEnabled = "mcpWriteEnabled"
        public static let mcpConfirmRenames = "mcpConfirmRenames"
        public static let semanticSearchEnabled = "semanticSearchEnabled"
    }

    /// Pfad aus den Einstellungen; "" = Standardordner.
    public var meetingFolderPath: String
    public var vocabularyEnabled: Bool
    public var vocabularyEntries: [VocabularyEntry]
    /// Anweisung für Zusammenfassungen (die App speichert nur Abweichungen vom Standard).
    public var summaryPromptTemplate: String
    /// Hauptschalter: Claude darf Meetings über luna-mcp lesen. Standard aus.
    public var mcpAccessEnabled: Bool
    /// Claude darf Meetings umbenennen, Sprecher benennen und (nach Bestätigung) löschen. Standard aus.
    public var mcpWriteEnabled: Bool
    /// Auch Umbenennungen erst nach einem Bestätigungsdialog ausführen. Standard aus.
    public var mcpConfirmRenames: Bool
    /// Inhaltliche Suche (Embedding-Modell) nutzen. Standard aus, weil sie einen Modell-Download braucht.
    public var semanticSearchEnabled: Bool

    public init(meetingFolderPath: String = "", vocabularyEnabled: Bool = true, vocabularyEntries: [VocabularyEntry] = [],
                summaryPromptTemplate: String = SummaryPrompt.defaultTemplate, mcpAccessEnabled: Bool = false,
                mcpWriteEnabled: Bool = false, mcpConfirmRenames: Bool = false, semanticSearchEnabled: Bool = false) {
        self.meetingFolderPath = meetingFolderPath
        self.vocabularyEnabled = vocabularyEnabled
        self.vocabularyEntries = vocabularyEntries
        self.summaryPromptTemplate = summaryPromptTemplate
        self.mcpAccessEnabled = mcpAccessEnabled
        self.mcpWriteEnabled = mcpWriteEnabled
        self.mcpConfirmRenames = mcpConfirmRenames
        self.semanticSearchEnabled = semanticSearchEnabled
    }

    public var meetingFolder: URL { AppPaths.meetingFolder(from: meetingFolderPath) }

    public static func read(from defaults: UserDefaults) -> SharedSettings {
        func bool(_ key: String, _ fallback: Bool) -> Bool {
            defaults.object(forKey: key) == nil ? fallback : defaults.bool(forKey: key)
        }
        let entries = defaults.data(forKey: Key.vocabularyEntries)
            .flatMap { try? JSONDecoder().decode([VocabularyEntry].self, from: $0) } ?? []
        return SharedSettings(
            meetingFolderPath: defaults.string(forKey: Key.meetingFolder) ?? "",
            vocabularyEnabled: bool(Key.vocabularyEnabled, true),
            vocabularyEntries: entries,
            summaryPromptTemplate: defaults.string(forKey: Key.summaryPrompt) ?? SummaryPrompt.defaultTemplate,
            mcpAccessEnabled: bool(Key.mcpAccessEnabled, false),
            mcpWriteEnabled: bool(Key.mcpWriteEnabled, false),
            mcpConfirmRenames: bool(Key.mcpConfirmRenames, false),
            semanticSearchEnabled: bool(Key.semanticSearchEnabled, false)
        )
    }

    /// Übernimmt einmalig alle Einstellungen der alten Bundle-ID (Wörterbuch, Hotkeys, Meeting-Ordner, Prompts …)
    /// in `defaults`. Schlüssel, die dort schon gesetzt sind, bleiben unangetastet; die alte Domäne bleibt erhalten.
    /// Muss laufen, bevor irgendetwas Einstellungen liest.
    @discardableResult
    public static func migrateLegacyDomain(into defaults: UserDefaults, legacyDomain: String = legacyAppDomain) -> Int {
        guard !defaults.bool(forKey: legacyMigrationKey) else { return 0 }
        var copied = 0
        for (key, value) in defaults.persistentDomain(forName: legacyDomain) ?? [:] where defaults.object(forKey: key) == nil {
            defaults.set(value, forKey: key)
            copied += 1
        }
        defaults.set(true, forKey: legacyMigrationKey)
        return copied
    }

    /// Liest die aktuellen Werte aus der Domäne der App – aus einem anderen Prozess (luna-mcp).
    public static func readAppDomain() -> SharedSettings {
        #if canImport(Darwin)
        // Ohne Synchronisieren liefert ein lange laufender Prozess sonst womöglich zwischengespeicherte Werte.
        CFPreferencesAppSynchronize(appDomain as CFString)
        #endif
        return read(from: UserDefaults(suiteName: appDomain) ?? .standard)
    }
}
