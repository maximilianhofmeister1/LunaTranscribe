import AppKit
import CoreText
import SwiftUI

// Design-System „Mondnacht“: warm und verspielt. Hell = Dämmerung (cremiges Mondlicht),
// dunkel = Mondnacht (Nachtblau). Alle Views nutzen nur diese Farben, Schriften und Maße.

// MARK: Farben

enum LunaColor {
    /// Fensterhintergrund
    static let background = dynamic(light: 0xFBF6EC, dark: 0x1A1830)
    /// Karten und Eingabeflächen
    static let surface = dynamic(light: 0xFFFDF8, dark: 0x26223F)
    /// Seitenleiste, zurückgenommene Flächen
    static let surfaceMuted = dynamic(light: 0xF3ECDD, dark: 0x211D38)
    /// Haupttext
    static let ink = dynamic(light: 0x2B2540, dark: 0xEDE6D6)
    /// Nebentext
    static let inkMuted = dynamic(light: 0x6E6682, dark: 0xA9A1BD)
    /// Markenakzent: Lavendel
    static let lavender = dynamic(light: 0x7E6BB8, dark: 0xB9A7F0)
    /// Warmes Honiggold: Aufnahme, Hervorhebungen
    static let honey = dynamic(light: 0xD9962A, dark: 0xE8B04B)
    /// Salbei: bereit, erfolgreich
    static let sage = dynamic(light: 0x5E9480, dark: 0x8FB9A8)
    /// Rosé: Fehler, Löschen
    static let rose = dynamic(light: 0xC0505D, dark: 0xE68A93)
    /// Haarlinien um Karten
    static let stroke = dynamic(light: 0x2B2540, dark: 0xEDE6D6, lightAlpha: 0.10, darkAlpha: 0.12)
    /// Text auf Lavendel-Flächen
    static let onAccent = dynamic(light: 0xFFFDF8, dark: 0x1A1830)

    /// Nachthimmel für Overlay und Hero-Flächen – in beiden Modi dunkel.
    static let nightTop = Color(hex: 0x3A2F6B)
    static let nightBottom = Color(hex: 0x15132B)
    static let moonlight = Color(hex: 0xFBF1DC)

    private static func dynamic(light: UInt32, dark: UInt32, lightAlpha: CGFloat = 1, darkAlpha: CGFloat = 1) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            return NSColor(hex: isDark ? dark : light, alpha: isDark ? darkAlpha : lightAlpha)
        })
    }
}

extension NSColor {
    convenience init(hex: UInt32, alpha: CGFloat = 1) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
    }
}

extension Color {
    init(hex: UInt32) { self.init(nsColor: NSColor(hex: hex)) }
}

// MARK: Schriften

/// Fraunces (Serif mit weichen, „wackligen“ Formen) für Überschriften, Figtree für Text.
/// Beide liegen im App-Bundle (SIL Open Font License) und werden beim Start registriert.
/// Fehlen sie, fällt alles still auf New York bzw. SF Pro Rounded zurück.
enum LunaFont {
    /// Große, verspielte Überschrift (Seitentitel, Begrüßung).
    static func display(_ size: CGFloat) -> Font { fraunces(size, weight: 520, softness: 100) }
    /// Abschnitts- und Kartentitel.
    static func title(_ size: CGFloat = 17) -> Font { fraunces(size, weight: 600, softness: 60) }
    /// Fließtext und Bedienelemente.
    static func text(_ size: CGFloat = 13, weight: Font.Weight = .regular) -> Font { figtree(size, weight: weight) }
    /// Nebentexte, Hinweise.
    static func caption(_ size: CGFloat = 11.5, weight: Font.Weight = .regular) -> Font { figtree(size, weight: weight) }

    /// Registriert die mitgelieferten Schriften für diesen Prozess. Einmal beim Start aufrufen.
    static func registerBundledFonts() {
        for url in Bundle.main.urls(forResourcesWithExtension: "ttf", subdirectory: nil) ?? [] {
            CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
        }
    }

    // Achsen-Tags der variablen Fonts (vier ASCII-Zeichen als Zahl).
    private static let weightAxis = 0x7767_6874      // 'wght'
    private static let opticalSizeAxis = 0x6F70_737A // 'opsz'
    private static let softnessAxis = 0x534F_4654    // 'SOFT'
    private static let wonkyAxis = 0x574F_4E4B       // 'WONK'

    private static func fraunces(_ size: CGFloat, weight: CGFloat, softness: CGFloat) -> Font {
        let variation: [Int: CGFloat] = [
            weightAxis: weight, opticalSizeAxis: min(max(size, 9), 144), softnessAxis: softness, wonkyAxis: 1,
        ]
        return variableFont("Fraunces-Regular", size: size, variation: variation)
            ?? .system(size: size, weight: .semibold, design: .serif)
    }

    private static func figtree(_ size: CGFloat, weight: Font.Weight) -> Font {
        variableFont("Figtree-Regular", size: size, variation: [weightAxis: figtreeWeight(weight)])
            ?? .system(size: size, weight: weight, design: .rounded)
    }

    private static func figtreeWeight(_ weight: Font.Weight) -> CGFloat {
        switch weight {
        case .medium: return 500
        case .semibold: return 600
        case .bold, .heavy, .black: return 700
        case .light, .thin, .ultraLight: return 300
        default: return 400
        }
    }

    private static func variableFont(_ name: String, size: CGFloat, variation: [Int: CGFloat]) -> Font? {
        guard let base = NSFont(name: name, size: size) else { return nil }
        let descriptor = base.fontDescriptor.addingAttributes([.variation: variation])
        return NSFont(descriptor: descriptor, size: size).map(Font.init)
    }
}

// MARK: Formatierung

extension Date {
    private static let german = Locale(identifier: "de_DE")

    /// „25. Sep. 2026, 16:54“
    var lunaDateTime: String { formatted(.dateTime.day().month(.abbreviated).year().hour().minute().locale(Self.german)) }
    /// „25. September 2026, 16:54“
    var lunaLongDateTime: String { formatted(.dateTime.day().month(.wide).year().hour().minute().locale(Self.german)) }
    /// „16:54“
    var lunaTime: String { formatted(.dateTime.hour().minute().locale(Self.german)) }
}

// MARK: Maße

enum LunaMetrics {
    static let cornerRadius: CGFloat = 16
    static let smallCornerRadius: CGFloat = 10
    static let cardPadding: CGFloat = 16
    static let pagePadding: CGFloat = 28
    static let spacing: CGFloat = 12
}
