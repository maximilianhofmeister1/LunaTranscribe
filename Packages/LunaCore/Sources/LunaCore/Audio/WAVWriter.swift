import Foundation

/// Schreibt Float32-Samples absturzsicher in eine WAV-Datei: Samples gehen sofort ans Betriebssystem,
/// und alle `flushInterval` Sekunden werden die Größen im Kopf nachgetragen und die Datei auf die
/// Platte synchronisiert. Nach einem Absturz oder `kill` fehlen so höchstens diese Sekunden (meist
/// nichts, weil das System die geschriebenen Daten auch ohne Synchronisieren behält); ein veralteter
/// Kopf wird beim nächsten Start mit `WAVFile.repair` korrigiert.
///
/// Alle Dateizugriffe laufen auf einer eigenen seriellen Queue – `append` kommt aus dem Audio-Thread
/// (Core-Audio-IOProc) und darf dort weder auf die Platte warten noch synchronisieren.
public final class WAVWriter: @unchecked Sendable {
    public let url: URL
    public let sampleRate: Int
    public let flushInterval: TimeInterval

    // Nur auf `queue` benutzt.
    private let queue = DispatchQueue(label: "at.aljoma.lunatranscribe.wavwriter", qos: .userInitiated)
    private var handle: FileHandle?
    private var dataBytes = 0
    private var lastFlush = Date()
    private var error: Swift.Error?

    public init(url: URL, sampleRate: Int, flushInterval: TimeInterval = 2) throws {
        self.url = url
        self.sampleRate = sampleRate
        self.flushInterval = flushInterval
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard FileManager.default.createFile(atPath: url.path,
                                             contents: WAVFile.header(sampleRate: sampleRate, dataBytes: 0)) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        let handle = try FileHandle(forWritingTo: url)
        try handle.seekToEnd()
        self.handle = handle
    }

    deinit { try? handle?.close() }

    /// Hängt Mono-Samples an (kopiert sie, kehrt sofort zurück).
    public func append(_ samples: UnsafeBufferPointer<Float>) {
        guard !samples.isEmpty else { return }
        // Alle unterstützten Plattformen (Apple Silicon, x86, Linux auf ARM/x86) sind little endian.
        let data = Data(buffer: samples)
        queue.async { self.write(data) }
    }

    public func append(_ samples: [Float]) {
        samples.withUnsafeBufferPointer { append($0) }
    }

    /// Kopf aktualisieren und synchronisieren; wartet, bis alles Vorherige geschrieben ist.
    public func flush() {
        queue.sync { flushHeader() }
    }

    /// Schließt die Datei mit vollständigem Kopf. Danach sind weitere `append` wirkungslos.
    public func close() {
        queue.sync {
            flushHeader()
            try? handle?.close()
            handle = nil
        }
    }

    /// Bisher geschriebene Audio-Bytes (ohne Kopf).
    public var writtenBytes: Int { queue.sync { dataBytes } }

    /// Erster Schreibfehler (z. B. Platte voll), danach wird nichts mehr geschrieben.
    public var writeError: Swift.Error? { queue.sync { error } }

    // MARK: Intern (auf `queue`)

    private func write(_ data: Data) {
        guard let handle, error == nil else { return }
        do {
            try handle.write(contentsOf: data)
            dataBytes += data.count
        } catch {
            self.error = error
            return
        }
        if Date().timeIntervalSince(lastFlush) >= flushInterval { flushHeader() }
    }

    private func flushHeader() {
        lastFlush = Date()
        guard let handle, error == nil else { return }
        do {
            let end = WAVFile.canonicalHeaderSize + dataBytes
            try handle.seek(toOffset: 4)
            try handle.write(contentsOf: Data(le: WAVFile.clampedUInt32(end - 8)))
            try handle.seek(toOffset: UInt64(WAVFile.canonicalHeaderSize - 4))
            try handle.write(contentsOf: Data(le: WAVFile.clampedUInt32(dataBytes)))
            try handle.seekToEnd()
            try handle.synchronize()
        } catch {
            self.error = error
        }
    }
}
