import KeyboardShortcuts

extension KeyboardShortcuts.Name {
    /// Haupt-Hotkey, Default F2 (ohne Modifier).
    static let recordPrimary = Self("recordPrimary", initial: .init(.f2))
    /// Optionaler zweiter Hotkey mit eigener Sprache. Kein Default.
    static let recordSecondary = Self("recordSecondary")
    /// Dritter Hotkey: Hauptsprache + Nachbearbeitung durch das Sprachmodell. Kein Default.
    static let recordRefined = Self("recordRefined")
}

enum HotkeySlot {
    case primary
    case secondary
    /// Diktat in der Hauptsprache, anschließend vom Sprachmodell bereinigt.
    case refined
}
