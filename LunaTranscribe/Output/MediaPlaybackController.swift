import Foundation

/// Pausiert laufende Medienwiedergabe (Musik, Spotify, Browser-Video …) während der Aufnahme
/// und setzt sie danach fort – aber nur, wenn wir sie selbst pausiert haben.
///
/// Technik: Seit macOS 15.4 dürfen normale Apps das MediaRemote-Framework nicht mehr direkt
/// nutzen. Der gebündelte MediaRemoteAdapter wird deshalb über `/usr/bin/perl` (Apple-signiert,
/// daher berechtigt) geladen. Siehe Vendor/MediaRemoteAdapter/README.md.
final class MediaPlaybackController: @unchecked Sendable {
    /// MRCommand-IDs aus MediaRemote.h
    private enum Command: String {
        case play = "0"
        case pause = "1"
    }

    private let scriptURL: URL?
    private let frameworkURL: URL?
    private var pausedByUs = false

    init() {
        scriptURL = Bundle.main.url(forResource: "mediaremote-adapter", withExtension: "pl")
        frameworkURL = Bundle.main.privateFrameworksURL?.appendingPathComponent("MediaRemoteAdapter.framework")
    }

    var isAvailable: Bool {
        guard let scriptURL, let frameworkURL else { return false }
        return FileManager.default.fileExists(atPath: scriptURL.path)
            && FileManager.default.fileExists(atPath: frameworkURL.path)
    }

    /// Pausiert, falls gerade etwas spielt. Dauert ~20 ms.
    func pauseIfPlaying() {
        pausedByUs = false
        guard isAvailable, isPlaying() else { return }
        if send(.pause) { pausedByUs = true }
    }

    /// Setzt fort, falls wir zuvor pausiert haben.
    func resumeIfPaused() {
        guard pausedByUs else { return }
        pausedByUs = false
        _ = send(.play)
    }

    // MARK: Adapter-Aufrufe

    private func isPlaying() -> Bool {
        guard let output = run(["get", "--no-artwork"]),
              let data = output.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let playing = json["playing"] as? Bool else { return false }
        return playing
    }

    @discardableResult
    private func send(_ command: Command) -> Bool {
        run(["send", command.rawValue]) != nil
    }

    /// Führt den Adapter synchron aus und gibt stdout zurück (nil bei Fehler).
    private func run(_ arguments: [String]) -> String? {
        guard let scriptURL, let frameworkURL else { return nil }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/perl")
        process.arguments = [scriptURL.path, frameworkURL.path] + arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return nil
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
