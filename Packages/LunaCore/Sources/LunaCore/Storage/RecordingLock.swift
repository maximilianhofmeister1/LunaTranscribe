import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Markiert eine laufende Aufnahme per `flock` auf einer Sperrdatei im Auftragsordner. Das System gibt
/// die Sperre frei, sobald der Prozess endet – auch bei Absturz oder `kill -9`. Damit lässt sich
/// eindeutig unterscheiden: Sperre gehalten = Aufnahme läuft (in diesem oder einem anderen Prozess,
/// z. B. App + Selbsttest per `open -n`); Sperre frei = Aufnahme wurde unterbrochen.
public final class RecordingLock: @unchecked Sendable {
    public let url: URL
    private let lock = NSLock()
    private var descriptor: Int32

    private init(url: URL, descriptor: Int32) {
        self.url = url
        self.descriptor = descriptor
    }

    deinit { release() }

    /// Übernimmt die Sperre (legt die Datei bei Bedarf an). nil, wenn sie schon jemand hält.
    public static func acquire(at url: URL) -> RecordingLock? {
        let fd = open(url.path, O_RDWR | O_CREAT, 0o644)
        guard fd >= 0 else { return nil }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            close(fd)
            return nil
        }
        return RecordingLock(url: url, descriptor: fd)
    }

    /// Hält gerade irgendein Prozess (auch dieser) die Sperre? Legt keine Datei an.
    public static func isHeld(at url: URL) -> Bool {
        let fd = open(url.path, O_RDONLY)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else { return true }
        flock(fd, LOCK_UN)
        return false
    }

    /// Gibt die Sperre frei; `removeFile` löscht die Sperrdatei vorher (Aufnahme ist abgeschlossen).
    public func release(removeFile: Bool = false) {
        lock.lock(); defer { lock.unlock() }
        guard descriptor >= 0 else { return }
        if removeFile { try? FileManager.default.removeItem(at: url) }
        flock(descriptor, LOCK_UN)
        close(descriptor)
        descriptor = -1
    }
}
