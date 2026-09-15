import Foundation
import AVFoundation
import Accelerate
import CoreAudio

/// Liest direkt per Core-Audio-IOProc von einem bestimmten Eingabegerät und liefert Float32-Puffer
/// im nativen Format des Geräts (alle Kanäle, non-interleaved).
///
/// Warum nicht `AVAudioEngine`? Deren `inputNode` übernimmt nach einem Gerätewechsel per
/// `kAudioOutputUnitProperty_CurrentDevice` das Kanal-Layout nicht – sie bleibt beim Format des
/// Standardgeräts (1 Kanal), obwohl der AUHAL bereits 3 Kanäle meldet. Für ein Aggregate aus
/// Mikrofon + Systemaudio-Tap ist das unbrauchbar. Der IOProc liefert dagegen pro Stream einen
/// Puffer (Mikrofon, Tap …), die hier zu einem planaren N-Kanal-Puffer zusammengesetzt werden.
final class DeviceInputStream {
    private let deviceID: AudioDeviceID
    private var procID: AudioDeviceIOProcID?
    private var handler: ((AVAudioPCMBuffer) -> Void)?
    /// Kanäle je Eingangs-Stream, in Stream-Reihenfolge (Aggregate: erst Sub-Devices, dann Taps).
    private let streamChannels: [Int]

    /// Format der gelieferten Puffer: alle Kanäle des Geräts, Float32, non-interleaved, Geräte-Samplerate.
    let format: AVAudioFormat

    init(deviceID: AudioDeviceID) throws {
        self.deviceID = deviceID
        let rate = try Self.nominalSampleRate(of: deviceID)
        streamChannels = try Self.inputStreamChannels(of: deviceID)
        let total = streamChannels.reduce(0, +)
        guard total > 0, rate > 0,
              let format = AVAudioFormat.discreteFloat32(channels: AVAudioChannelCount(total), sampleRate: rate) else {
            throw AudioRecorderError.deviceUnavailable("Kanäle \(streamChannels), Rate \(rate)")
        }
        self.format = format
    }

    deinit { stop() }

    func start(handler: @escaping (AVAudioPCMBuffer) -> Void) throws {
        self.handler = handler
        var proc: AudioDeviceIOProcID?
        let status = AudioDeviceCreateIOProcIDWithBlock(&proc, deviceID, nil) { [weak self] _, inputData, _, _, _ in
            self?.deliver(inputData)
        }
        guard status == noErr, let proc else { throw AudioRecorderError.deviceUnavailable("IOProc-Fehler \(status)") }
        procID = proc
        let startStatus = AudioDeviceStart(deviceID, proc)
        guard startStatus == noErr else {
            AudioDeviceDestroyIOProcID(deviceID, proc)
            procID = nil
            throw AudioRecorderError.deviceUnavailable("Start-Fehler \(startStatus)")
        }
    }

    func stop() {
        guard let proc = procID else { return }
        AudioDeviceStop(deviceID, proc)
        AudioDeviceDestroyIOProcID(deviceID, proc)
        procID = nil
        handler = nil
    }

    // MARK: IOProc

    /// Kopiert die (je Stream interleaved) Gerätepuffer in einen planaren Puffer mit allen Kanälen.
    private func deliver(_ inputData: UnsafePointer<AudioBufferList>) {
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inputData))
        guard let first = buffers.first, first.mNumberChannels > 0 else { return }
        let frames = Int(first.mDataByteSize) / (Int(first.mNumberChannels) * MemoryLayout<Float>.size)
        guard frames > 0,
              let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let dst = out.floatChannelData else { return }
        out.frameLength = AVAudioFrameCount(frames)

        var channelIndex = 0
        for buffer in buffers {
            let channels = Int(buffer.mNumberChannels)
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { continue }
            let available = Int(buffer.mDataByteSize) / (channels * MemoryLayout<Float>.size)
            for c in 0..<channels where channelIndex < Int(format.channelCount) {
                let target = dst[channelIndex]
                let n = min(frames, available)
                cblas_scopy(Int32(n), data + c, Int32(channels), target, 1)
                if n < frames { vDSP_vclr(target + n, 1, vDSP_Length(frames - n)) }
                channelIndex += 1
            }
        }
        handler?(out)
    }

    // MARK: CoreAudio-Abfragen

    private static func nominalSampleRate(of id: AudioDeviceID) throws -> Double {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var rate: Double = 0
        var size = UInt32(MemoryLayout<Double>.size)
        let status = AudioObjectGetPropertyData(id, &address, 0, nil, &size, &rate)
        guard status == noErr else { throw AudioRecorderError.deviceUnavailable("Samplerate-Abfrage \(status)") }
        return rate
    }

    private static func inputStreamChannels(of id: AudioDeviceID) throws -> [Int] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams,
            mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        let sizeStatus = AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size)
        guard sizeStatus == noErr, size > 0 else {
            throw AudioRecorderError.deviceUnavailable("keine Eingangs-Streams (Status \(sizeStatus))")
        }
        var streams = [AudioStreamID](repeating: 0, count: Int(size) / MemoryLayout<AudioStreamID>.size)
        let dataStatus = AudioObjectGetPropertyData(id, &address, 0, nil, &size, &streams)
        guard dataStatus == noErr else {
            throw AudioRecorderError.deviceUnavailable("Stream-Liste (Status \(dataStatus))")
        }

        return try streams.map { stream in
            var formatAddress = AudioObjectPropertyAddress(
                mSelector: kAudioStreamPropertyVirtualFormat,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var asbd = AudioStreamBasicDescription()
            var asbdSize = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            let status = AudioObjectGetPropertyData(stream, &formatAddress, 0, nil, &asbdSize, &asbd)
            guard status == noErr,
                  asbd.mFormatID == kAudioFormatLinearPCM,
                  asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0,
                  asbd.mBitsPerChannel == 32 else {
                // Das virtuelle Format ist auf macOS praktisch immer Float32 – alles andere ist unerwartet.
                throw AudioRecorderError.deviceUnavailable(
                    "Stream-Format (Status \(status), ID \(asbd.mFormatID), Flags \(asbd.mFormatFlags), \(asbd.mBitsPerChannel) Bit)")
            }
            return Int(asbd.mChannelsPerFrame)
        }
    }
}
