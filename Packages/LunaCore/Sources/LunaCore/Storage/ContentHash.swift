import Foundation

/// FNV-1a (64 Bit) über Bytes. Dient als Revision (optimistische Sperre) und als Änderungsmerkmal für den
/// Suchindex – nicht als Sicherheitsmerkmal. Kollisionen sind für diesen Zweck praktisch ausgeschlossen,
/// und es braucht kein Krypto-Framework, das es unter Linux nicht gibt.
public enum ContentHash {
    public static func hex(_ data: Data) -> String {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in data {
            hash ^= UInt64(byte)
            hash = hash &* 0x0000_0100_0000_01b3
        }
        return String(format: "%016llx", hash)
    }

    public static func hex(_ string: String) -> String { hex(Data(string.utf8)) }
}
