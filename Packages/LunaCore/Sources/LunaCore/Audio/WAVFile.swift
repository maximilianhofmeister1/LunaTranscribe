import Foundation

/// Lesen, Prüfen und Reparieren von WAV-Köpfen (RIFF). Reine Byte-Arbeit ohne AVFoundation, damit
/// die Reparatur auch unter Linux getestet werden kann.
///
/// Hintergrund: Ein WAV-Kopf enthält die Größe der Audiodaten. Bricht die Aufnahme hart ab (Absturz,
/// `kill`, Strom weg), steht dort 0 oder ein veralteter Wert, obwohl die Samples auf der Platte liegen.
/// `repair` rechnet die Größen aus der Dateigröße neu aus.
public enum WAVFile {
    public enum Error: Swift.Error, LocalizedError, Equatable {
        case notWAV
        case missingChunk(String)
        case unsupportedFormat(String)

        public var errorDescription: String? {
            switch self {
            case .notWAV: return "Keine WAV-Datei."
            case .missingChunk(let id): return "WAV-Datei ohne „\(id)“-Abschnitt."
            case .unsupportedFormat(let detail): return "WAV-Format nicht unterstützt: \(detail)"
            }
        }
    }

    public struct Info: Equatable, Sendable {
        public var sampleRate: Int
        public var channels: Int
        /// Bytes pro Frame (alle Kanäle).
        public var blockAlign: Int
        /// Beginn der Audiodaten in der Datei.
        public var dataOffset: Int
        /// Laut Kopf.
        public var declaredDataBytes: Int
        /// Tatsächlich vorhandene, vollständige Frames ab `dataOffset` bis Dateiende.
        public var availableDataBytes: Int

        public var frames: Int { availableDataBytes / max(blockAlign, 1) }
        public var durationSeconds: TimeInterval { sampleRate > 0 ? Double(frames) / Double(sampleRate) : 0 }
        /// Kopf passt zur Datei (nach `repair` immer true).
        public var isConsistent: Bool { declaredDataBytes == availableDataBytes }
    }

    /// Größe des Kopfes, den `header` erzeugt (RIFF + fmt(16) + data).
    public static let canonicalHeaderSize = 44
    /// WAVE_FORMAT_IEEE_FLOAT
    static let formatFloat: UInt16 = 3

    /// Kanonischer 44-Byte-Kopf für Float32-PCM (little endian), wie ihn `WAVWriter` schreibt.
    public static func header(sampleRate: Int, channels: Int = 1, dataBytes: Int) -> Data {
        let blockAlign = channels * 4
        let dataSize = clampedUInt32(dataBytes)
        var data = Data(capacity: canonicalHeaderSize)
        data.append(ascii: "RIFF")
        data.append(le: clampedUInt32(Int(dataSize) + canonicalHeaderSize - 8))
        data.append(ascii: "WAVE")
        data.append(ascii: "fmt ")
        data.append(le: UInt32(16))
        data.append(le: formatFloat)
        data.append(le: UInt16(channels))
        data.append(le: UInt32(sampleRate))
        data.append(le: UInt32(sampleRate * blockAlign))
        data.append(le: UInt16(blockAlign))
        data.append(le: UInt16(32))
        data.append(ascii: "data")
        data.append(le: dataSize)
        return data
    }

    /// Liest den Kopf. Der `data`-Abschnitt gilt als letzter Abschnitt der Datei – so schreiben alle
    /// Aufnahme-Writer (Apple `AudioFile`, `WAVWriter`), und nur dann ist eine Reparatur eindeutig.
    public static func inspect(at url: URL) throws -> Info {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let fileSize = Int(try handle.seekToEnd())
        try handle.seek(toOffset: 0)
        // Köpfe sind klein; Apple schreibt JUNK/FLLR-Füllabschnitte, zusammen weit unter 64 KB.
        let head = try handle.read(upToCount: 64 * 1024) ?? Data()
        return try parse(head, fileSize: fileSize)
    }

    /// Repariert den Kopf einer abgebrochenen Aufnahme: RIFF- und data-Größe aus der Dateigröße,
    /// ein angefangenes letztes Frame wird abgeschnitten. Unveränderte Dateien bleiben unberührt.
    @discardableResult
    public static func repair(at url: URL) throws -> Info {
        var info = try inspect(at: url)
        let end = info.dataOffset + info.availableDataBytes
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        let fileSize = Int(try handle.seekToEnd())
        guard !info.isConsistent || fileSize != end else { return info }

        if fileSize > end { try handle.truncate(atOffset: UInt64(end)) }
        try handle.seek(toOffset: 4)
        try handle.write(contentsOf: Data(le: clampedUInt32(end - 8)))
        try handle.seek(toOffset: UInt64(info.dataOffset - 4))
        try handle.write(contentsOf: Data(le: clampedUInt32(info.availableDataBytes)))
        try handle.synchronize()
        info.declaredDataBytes = info.availableDataBytes
        return info
    }

    // MARK: Intern

    static func parse(_ head: Data, fileSize: Int) throws -> Info {
        let bytes = [UInt8](head)
        guard bytes.count >= 12, ascii(bytes, 0) == "RIFF", ascii(bytes, 8) == "WAVE" else { throw Error.notWAV }

        var format: (rate: Int, channels: Int, blockAlign: Int)?
        var offset = 12
        while offset + 8 <= bytes.count {
            let id = ascii(bytes, offset)
            let size = Int(uint32(bytes, offset + 4))
            let payload = offset + 8
            if id == "data" {
                guard let format else { throw Error.missingChunk("fmt ") }
                let available = max(0, fileSize - payload) / format.blockAlign * format.blockAlign
                return Info(sampleRate: format.rate, channels: format.channels, blockAlign: format.blockAlign,
                            dataOffset: payload, declaredDataBytes: size, availableDataBytes: available)
            }
            if id == "fmt " {
                guard payload + 16 <= bytes.count else { throw Error.missingChunk("fmt ") }
                let channels = Int(uint16(bytes, payload + 2))
                let rate = Int(uint32(bytes, payload + 4))
                let blockAlign = Int(uint16(bytes, payload + 12))
                guard channels > 0, rate > 0, blockAlign > 0 else {
                    throw Error.unsupportedFormat("\(channels) Kanäle, \(rate) Hz, Block \(blockAlign)")
                }
                format = (rate, channels, blockAlign)
            }
            offset = payload + size + (size & 1)   // Abschnitte sind auf gerade Längen aufgefüllt
        }
        throw Error.missingChunk("data")
    }

    private static func ascii(_ bytes: [UInt8], _ at: Int) -> String {
        String(decoding: bytes[at..<at + 4], as: UTF8.self)
    }

    private static func uint16(_ bytes: [UInt8], _ at: Int) -> UInt16 {
        UInt16(bytes[at]) | UInt16(bytes[at + 1]) << 8
    }

    private static func uint32(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        (0..<4).reduce(UInt32(0)) { $0 | UInt32(bytes[at + $1]) << (8 * UInt32($1)) }
    }

    /// RIFF-Größen sind 32 Bit – bei 16 kHz mono Float32 reicht das für gut 18 Stunden.
    static func clampedUInt32(_ value: Int) -> UInt32 { UInt32(clamping: max(0, value)) }
}

extension Data {
    mutating func append(ascii: String) { append(contentsOf: Array(ascii.utf8)) }

    mutating func append<T: FixedWidthInteger>(le value: T) {
        Swift.withUnsafeBytes(of: value.littleEndian) { append(contentsOf: $0) }
    }

    init<T: FixedWidthInteger>(le value: T) {
        self.init()
        append(le: value)
    }
}
