import Foundation
import CoreAudio
import os

enum SystemAudioTapError: LocalizedError {
    case noMicrophone
    case noOutputDevice
    case tapFailed(OSStatus)
    case aggregateFailed(OSStatus)

    var errorDescription: String? {
        switch self {
        case .noMicrophone: return "Kein Mikrofon für die Meeting-Aufnahme gefunden."
        case .noOutputDevice: return "Kein Ausgabegerät gefunden – Systemaudio kann nicht abgegriffen werden."
        case .tapFailed(let s): return "Systemaudio konnte nicht abgegriffen werden (Fehler \(s))."
        case .aggregateFailed(let s): return "Aufnahmegerät konnte nicht erstellt werden (Fehler \(s))."
        }
    }
}

/// Greift die Systemwiedergabe (Zoom, Teams, Browser …) per Core-Audio-Prozess-Tap ab und kombiniert
/// sie mit dem Mikrofon zu einem privaten Aggregate-Device. Core Audio synchronisiert dabei beide
/// Uhren selbst (Drift-Kompensation), sodass der `AudioRecorder` nur ein einziges Gerät sieht.
///
/// Braucht die Berechtigung „Systemaudio aufnehmen“ (`NSAudioCaptureUsageDescription`); der Dialog
/// erscheint beim ersten Erzeugen des Taps. Wird sie verweigert, liefert der Tap Stille – Core Audio
/// meldet dafür keinen Fehler, deshalb weist die App im Menü auf die Einstellung hin.
/// Verfügbar ab macOS 14.2; Deployment-Target ist 15.0.
final class SystemAudioTap {
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)

    deinit { destroy() }

    /// Erzeugt Tap + Aggregate-Device und beschreibt, wie der Recorder die Kanäle mischen soll.
    /// - Parameter microphoneUID: leer/nil = System-Standardmikrofon
    func makeCaptureSource(microphoneUID: String?) throws -> CaptureSource {
        destroy()

        guard let mic = Self.resolveMicrophone(uid: microphoneUID) else { throw SystemAudioTapError.noMicrophone }
        let micChannels = max(1, AudioDevices.inputChannelCount(of: mic.id))

        // Eigenen Prozess ausschließen: sonst landen die Start/Stopp-Töne der App in der Aufnahme.
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [Self.ownProcessObjectID()].compactMap { $0 })
        description.name = "LunaTranscribe Meeting"
        description.isPrivate = true
        description.muteBehavior = .unmuted   // Der Nutzer soll das Meeting weiterhin hören.

        var tap = AudioObjectID(kAudioObjectUnknown)
        let tapStatus = AudioHardwareCreateProcessTap(description, &tap)
        guard tapStatus == noErr, tap != kAudioObjectUnknown else { throw SystemAudioTapError.tapFailed(tapStatus) }
        tapID = tap

        // Der Tap hängt am IO-Zyklus des Ausgabegeräts. Deshalb ist das Ausgabegerät Master und
        // Clock des Aggregates (so macht es auch AudioCap) – mit dem Mikrofon als Master startete
        // das Aggregate nur unzuverlässig. Das Mikrofon läuft als Sub-Device mit Drift-Kompensation.
        // Kanalreihenfolge der Eingänge: Sub-Devices in Listenreihenfolge (Ausgabegerät hat keine
        // Eingänge → Mikrofon zuerst), danach die Taps.
        guard let output = AudioDevices.defaultOutputDeviceUID() else { throw SystemAudioTapError.noOutputDevice }
        var subDevices: [[String: Any]] = [[kAudioSubDeviceUIDKey: output]]
        if mic.uid != output {
            subDevices.append([kAudioSubDeviceUIDKey: mic.uid, kAudioSubDeviceDriftCompensationKey: true])
        }

        let aggregateDescription: [String: Any] = [
            kAudioAggregateDeviceNameKey: "LunaTranscribe Meeting",
            kAudioAggregateDeviceUIDKey: "at.aljoma.lunatranscribe.meeting.\(UUID().uuidString)",
            kAudioAggregateDeviceIsPrivateKey: true,           // taucht nicht in den Systemeinstellungen auf
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceMainSubDeviceKey: output,
            kAudioAggregateDeviceSubDeviceListKey: subDevices,
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapUIDKey: description.uuid.uuidString,
                kAudioSubTapDriftCompensationKey: true,
            ]],
            // WICHTIG: „tapautostart“ = AudioDeviceStart wartet, bis ein abgegriffener Prozess erstmals
            // Audio ausgibt. Für eine Meeting-Aufnahme, die auch bei Stille laufen muss, ist das falsch –
            // das Aggregate lief dann nur, sobald irgendwo Audio spielte, und die Aufnahme blieb leer.
            kAudioAggregateDeviceTapAutoStartKey: false,
        ]

        var aggregate = AudioObjectID(kAudioObjectUnknown)
        let aggStatus = AudioHardwareCreateAggregateDevice(aggregateDescription as CFDictionary, &aggregate)
        guard aggStatus == noErr, aggregate != kAudioObjectUnknown else {
            destroy()
            throw SystemAudioTapError.aggregateFailed(aggStatus)
        }
        aggregateID = aggregate

        // Kanalreihenfolge im Aggregate: erst die Sub-Devices (Mikrofon), dann die Taps (Stereo).
        // Die tatsächliche Kanalzahl wird abgefragt, statt sie anzunehmen – so fällt sofort auf,
        // wenn der Tap nicht eingehängt wurde (z. B. Berechtigung fehlt).
        let totalChannels = AudioDevices.inputChannelCount(of: aggregate)
        let tapChannels = tapFormatChannels()
        Log.systemAudio.notice("tap+aggregate ready: mic=\(mic.name, privacy: .public) micChannels=\(micChannels) tapChannels=\(tapChannels) aggregateInputChannels=\(totalChannels)")

        let micGroup = Array(0..<micChannels)
        let tapGroup = Array(micChannels..<max(micChannels, totalChannels))
        return CaptureSource(deviceID: aggregate, channelGroups: tapGroup.isEmpty ? [micGroup] : [micGroup, tapGroup])
    }

    /// Kanalzahl des Tap-Streams (0 = nicht abfragbar).
    private func tapFormatChannels() -> Int {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioTapPropertyFormat,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var format = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        guard AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &format) == noErr else { return 0 }
        return Int(format.mChannelsPerFrame)
    }

    /// Gibt Aggregate-Device und Tap wieder frei. Mehrfachaufruf ist unschädlich.
    func destroy() {
        if aggregateID != kAudioObjectUnknown {
            AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = AudioObjectID(kAudioObjectUnknown)
        }
        if tapID != kAudioObjectUnknown {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
    }

    // MARK: Helfer

    private static func resolveMicrophone(uid: String?) -> AudioInputDevice? {
        if let uid, !uid.isEmpty, let device = AudioDevices.inputDevices().first(where: { $0.uid == uid }) {
            return device
        }
        return AudioDevices.defaultInputDevice()
    }

    /// Core Audio adressiert Prozesse nicht per PID, sondern per eigenem Objekt.
    private static func ownProcessObjectID() -> AudioObjectID? {
        var pid = ProcessInfo.processInfo.processIdentifier
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        let status = withUnsafePointer(to: &pid) { pidPointer in
            AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                       UInt32(MemoryLayout<pid_t>.size), pidPointer, &size, &object)
        }
        guard status == noErr, object != kAudioObjectUnknown else { return nil }
        return object
    }
}
