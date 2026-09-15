import Foundation
import AVFoundation
import Accelerate
import CoreAudio

enum AudioRecorderError: LocalizedError {
    case noInput
    case formatUnavailable

    var errorDescription: String? {
        switch self {
        case .noInput: return "Kein Mikrofon gefunden."
        case .formatUnavailable: return "Audioformat konnte nicht eingerichtet werden."
        }
    }
}

/// Nimmt vom Mikrofon auf und wandelt live nach 16 kHz / mono / Float32.
/// Die Samples bleiben ausschließlich im RAM und werden mit `stop()` herausgegeben und verworfen.
final class AudioRecorder {
    static let targetSampleRate: Double = 16_000

    private var engine: AVAudioEngine?
    private let lock = NSLock()
    private var samples: [Float] = []

    /// Aktueller Spitzenpegel 0…1 (für die Pegelanzeige im Overlay).
    private(set) var level: Float = 0

    /// Wird einmal pro Aufnahme aufgerufen, sobald das erste Audio-Paket ankommt (Diagnose).
    var onFirstBuffer: (() -> Void)?
    private var firstBufferSeen = false

    var isRecording: Bool { engine?.isRunning ?? false }

    /// - Parameter deviceUID: leer/nil = System-Standardmikrofon
    func start(deviceUID: String?) throws {
        // Pro Aufnahme eine frische Engine: folgt so automatisch dem aktuellen Standardgerät.
        let engine = AVAudioEngine()
        let input = engine.inputNode

        if let uid = deviceUID, !uid.isEmpty,
           var deviceID = AudioDevices.deviceID(forUID: uid),
           let unit = input.audioUnit {
            AudioUnitSetProperty(unit,
                                 kAudioOutputUnitProperty_CurrentDevice,
                                 kAudioUnitScope_Global, 0,
                                 &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size))
        }

        let inFormat = input.outputFormat(forBus: 0)
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0 else { throw AudioRecorderError.noInput }

        guard let outFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                            sampleRate: Self.targetSampleRate,
                                            channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inFormat, to: outFormat) else {
            throw AudioRecorderError.formatUnavailable
        }

        lock.lock(); samples.removeAll(keepingCapacity: true); lock.unlock()
        level = 0
        firstBufferSeen = false

        input.installTap(onBus: 0, bufferSize: 4096, format: inFormat) { [weak self] buffer, _ in
            self?.append(buffer, converter: converter, outFormat: outFormat)
        }

        engine.prepare()
        try engine.start()
        self.engine = engine
    }

    /// Stoppt die Aufnahme und gibt die Samples zurück. Der interne Puffer wird geleert.
    func stop() -> [Float] {
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil

        lock.lock()
        let out = samples
        samples = []
        lock.unlock()
        level = 0
        return out
    }

    private func append(_ buffer: AVAudioPCMBuffer, converter: AVAudioConverter, outFormat: AVAudioFormat) {
        let ratio = outFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 64
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else { return }

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
        guard status != .error, let channel = out.floatChannelData?[0] else { return }

        let n = Int(out.frameLength)
        guard n > 0 else { return }

        if !firstBufferSeen {
            firstBufferSeen = true
            onFirstBuffer?()
        }

        var peak: Float = 0
        vDSP_maxmgv(channel, 1, &peak, vDSP_Length(n))
        level = min(1, peak)

        lock.lock()
        samples.append(contentsOf: UnsafeBufferPointer(start: channel, count: n))
        lock.unlock()
    }
}
