import Foundation
import AVFoundation
import Accelerate
import CoreAudio

enum AudioRecorderError: LocalizedError {
    case noInput
    case formatUnavailable
    case fileUnwritable(String)
    case deviceUnavailable(String)

    var errorDescription: String? {
        switch self {
        case .noInput: return "Kein Mikrofon gefunden."
        case .formatUnavailable: return "Audioformat konnte nicht eingerichtet werden."
        case .fileUnwritable(let path): return "Aufnahmedatei konnte nicht angelegt werden: \(path)"
        case .deviceUnavailable(let detail): return "Aufnahmegerät nicht nutzbar: \(detail)"
        }
    }
}

/// Wohin die 16-kHz-Samples fließen.
enum RecordingSink {
    /// Im RAM puffern; `stop()` gibt die Samples zurück (Diktat, wenige Sekunden).
    case memory
    /// Direkt in eine WAV-Datei schreiben (Meeting) – RAM bleibt konstant, egal wie lang die Aufnahme dauert.
    case file(URL)
}

/// Beschreibt, von welchem Gerät aufgenommen wird und wie dessen Kanäle zu mono gemischt werden.
struct CaptureSource {
    /// nil = System-Standardmikrofon
    var deviceID: AudioDeviceID?
    /// Kanalgruppen, die getrennt gemittelt und dann summiert werden – z. B. `[[0], [1, 2]]` für
    /// [Mikrofon], [Systemaudio L/R]. So bleibt jede Quelle gleich laut, statt dass ein Mono-Mikro
    /// gegen ein Stereo-Signal untergeht. nil = alle Kanäle gleichwertig mitteln.
    var channelGroups: [[Int]]?

    /// Mikrofon per Geräte-UID (leer/nil = Systemstandard).
    static func microphone(uid: String?) -> CaptureSource {
        guard let uid, !uid.isEmpty, let id = AudioDevices.deviceID(forUID: uid) else {
            return CaptureSource(deviceID: nil, channelGroups: nil)
        }
        return CaptureSource(deviceID: id, channelGroups: nil)
    }
}

/// Nimmt von einem Eingabegerät auf und wandelt live nach 16 kHz / mono / Float32.
/// Zwei Backends: das Standardmikrofon läuft über `AVAudioEngine` (folgt automatisch dem aktuellen
/// Standardgerät), explizite Geräte über `DeviceInputStream` (Core-Audio-IOProc), weil `AVAudioEngine`
/// nach einem Gerätewechsel das Kanal-Layout nicht übernimmt – nötig für das Meeting-Aggregate.
/// Mehrere Instanzen dürfen parallel laufen (macOS erlaubt mehrere Clients pro Eingabegerät) –
/// so kann während eines Meetings diktiert werden.
final class AudioRecorder {
    static let targetSampleRate: Double = 16_000

    private enum Backend {
        case engine(AVAudioEngine)
        case device(DeviceInputStream)
    }

    private var backend: Backend?
    private let lock = NSLock()
    private var samples: [Float] = []
    private var file: AVAudioFile?
    private var recordedFrames = 0

    /// Aktueller Spitzenpegel 0…1 (für die Pegelanzeige im Overlay).
    private(set) var level: Float = 0
    /// Kanalzahl des Eingabegeräts der laufenden Aufnahme (Diagnose: Aggregate = Mikro + 2 Tap-Kanäle).
    private(set) var inputChannelCount = 0
    /// Höchster Pegel je Eingangskanal seit Aufnahmestart (Diagnose: kommt auf dem Tap etwas an?).
    private(set) var channelPeaks: [Float] = []

    /// Wird einmal pro Aufnahme aufgerufen, sobald das erste Audio-Paket ankommt (Diagnose).
    var onFirstBuffer: (() -> Void)?
    private var firstBufferSeen = false

    var isRecording: Bool { backend != nil }

    /// Bisher aufgenommene Dauer in Sekunden (auch bei `.file`-Sink verfügbar).
    var recordedSeconds: Double {
        lock.lock(); defer { lock.unlock() }
        return Double(recordedFrames) / Self.targetSampleRate
    }

    /// Kurzform für Diktate: Mikrofon per UID, Samples im RAM.
    func start(deviceUID: String?) throws {
        try start(source: .microphone(uid: deviceUID), sink: .memory)
    }

    func start(source: CaptureSource, sink: RecordingSink) throws {
        // Backend wählen und dessen natives Eingangsformat ermitteln.
        let engine: AVAudioEngine?
        let deviceStream: DeviceInputStream?
        let inFormat: AVAudioFormat
        if let deviceID = source.deviceID {
            let stream = try DeviceInputStream(deviceID: deviceID)
            deviceStream = stream
            engine = nil
            inFormat = stream.format
        } else {
            // Pro Aufnahme eine frische Engine: folgt so automatisch dem aktuellen Standardgerät.
            let e = AVAudioEngine()
            engine = e
            deviceStream = nil
            inFormat = e.inputNode.outputFormat(forBus: 0)
        }
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0 else { throw AudioRecorderError.noInput }
        inputChannelCount = Int(inFormat.channelCount)
        channelPeaks = [Float](repeating: 0, count: inputChannelCount)

        // Erst Resampling mit allen Kanälen, dann eigene Mischung nach mono – so bleibt die
        // Gewichtung der Kanalgruppen (Mikro vs. Systemaudio) unter unserer Kontrolle.
        guard let resampled = AVAudioFormat.discreteFloat32(channels: inFormat.channelCount,
                                                            sampleRate: Self.targetSampleRate),
              let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                       sampleRate: Self.targetSampleRate,
                                       channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inFormat, to: resampled) else {
            throw AudioRecorderError.formatUnavailable
        }

        let groups = source.channelGroups ?? [Array(0..<Int(inFormat.channelCount))]

        lock.lock()
        samples.removeAll(keepingCapacity: true)
        recordedFrames = 0
        file = nil
        lock.unlock()
        level = 0
        firstBufferSeen = false

        if case .file(let url) = sink {
            let file = try Self.openWavForWriting(at: url, format: mono)
            lock.lock(); self.file = file; lock.unlock()
        }

        let handler: (AVAudioPCMBuffer) -> Void = { [weak self] buffer in
            self?.append(buffer, converter: converter, resampled: resampled, mono: mono, groups: groups)
        }

        if let engine {
            engine.inputNode.installTap(onBus: 0, bufferSize: 4096, format: inFormat) { buffer, _ in handler(buffer) }
            engine.prepare()
            try engine.start()
            backend = .engine(engine)
        } else if let deviceStream {
            try deviceStream.start(handler: handler)
            backend = .device(deviceStream)
        }
    }

    /// Stoppt die Aufnahme. Bei `.memory` kommen die Samples zurück und der Puffer wird geleert;
    /// bei `.file` ist das Ergebnis leer und die Datei ist geschlossen.
    func stop() -> [Float] {
        switch backend {
        case .engine(let engine):
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        case .device(let stream):
            stream.stop()
        case nil:
            break
        }
        backend = nil

        lock.lock()
        let out = samples
        samples = []
        file = nil   // AVAudioFile schließt beim Freigeben und schreibt den Header fertig
        lock.unlock()
        level = 0
        return out
    }

    // MARK: Intern

    private static func openWavForWriting(at url: URL, format: AVAudioFormat) throws -> AVAudioFile {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: format.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ]
        do {
            return try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        } catch {
            throw AudioRecorderError.fileUnwritable(url.path)
        }
    }

    private func append(_ buffer: AVAudioPCMBuffer, converter: AVAudioConverter,
                        resampled: AVAudioFormat, mono: AVAudioFormat, groups: [[Int]]) {
        let ratio = resampled.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: resampled, frameCapacity: capacity) else { return }

        var delivered = false
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, outStatus in
            if delivered {
                outStatus.pointee = .noDataNow
                return nil
            }
            delivered = true
            outStatus.pointee = .haveData
            return buffer
        }
        guard status != .error, let channels = out.floatChannelData else { return }

        let n = Int(out.frameLength)
        for c in 0..<min(channelPeaks.count, Int(resampled.channelCount)) {
            var peak: Float = 0
            vDSP_maxmgv(channels[c], 1, &peak, vDSP_Length(n))
            channelPeaks[c] = max(channelPeaks[c], peak)
        }
        guard n > 0, let monoBuffer = Self.mixToMono(channels, channelCount: Int(resampled.channelCount),
                                                     frames: n, groups: groups, format: mono) else { return }
        let monoSamples = monoBuffer.floatChannelData![0]

        if !firstBufferSeen {
            firstBufferSeen = true
            onFirstBuffer?()
        }

        var peak: Float = 0
        vDSP_maxmgv(monoSamples, 1, &peak, vDSP_Length(n))
        level = min(1, peak)

        lock.lock()
        defer { lock.unlock() }
        recordedFrames += n
        if let file {
            try? file.write(from: monoBuffer)
        } else {
            samples.append(contentsOf: UnsafeBufferPointer(start: monoSamples, count: n))
        }
    }

    /// Mischt N Kanäle nach mono: jede Gruppe wird gemittelt, die Gruppen werden summiert und auf ±1 begrenzt.
    private static func mixToMono(_ channels: UnsafePointer<UnsafeMutablePointer<Float>>, channelCount: Int,
                                  frames: Int, groups: [[Int]], format: AVAudioFormat) -> AVAudioPCMBuffer? {
        guard let mono = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let dst = mono.floatChannelData?[0] else { return nil }
        mono.frameLength = AVAudioFrameCount(frames)
        let length = vDSP_Length(frames)

        if channelCount == 1 {
            dst.update(from: channels[0], count: frames)
            return mono
        }

        vDSP_vclr(dst, 1, length)
        var groupSum = [Float](repeating: 0, count: frames)
        for group in groups {
            let valid = group.filter { $0 >= 0 && $0 < channelCount }
            guard !valid.isEmpty else { continue }
            vDSP_vclr(&groupSum, 1, length)
            for channel in valid {
                vDSP_vadd(groupSum, 1, channels[channel], 1, &groupSum, 1, length)
            }
            var scale = 1 / Float(valid.count)
            vDSP_vsma(groupSum, 1, &scale, dst, 1, dst, 1, length)
        }
        var low: Float = -1, high: Float = 1
        vDSP_vclip(dst, 1, &low, &high, dst, 1, length)
        return mono
    }
}

extension AVAudioFormat {
    /// Float32, non-interleaved, beliebige Kanalzahl. Der Convenience-Initializer mit `channels:`
    /// liefert für mehr als 2 Kanäle `nil` – dafür braucht es ein explizites (diskretes) Kanal-Layout.
    static func discreteFloat32(channels: AVAudioChannelCount, sampleRate: Double) -> AVAudioFormat? {
        guard channels > 0 else { return nil }
        if channels <= 2 {
            return AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: channels, interleaved: false)
        }
        guard let layout = AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | channels) else { return nil }
        return AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, interleaved: false, channelLayout: layout)
    }
}
