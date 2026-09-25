import Foundation
import AVFoundation
import os

struct MeetingProgress {
    let step: String
    /// 0…1 über die gesamte Verarbeitung
    let fraction: Double
}

enum MeetingTranscriberError: LocalizedError {
    case noSpeech
    case audioUnreadable

    var errorDescription: String? {
        switch self {
        case .noSpeech: return "Im Meeting wurde keine Sprache erkannt."
        case .audioUnreadable: return "Die Aufnahmedatei konnte nicht gelesen werden."
        }
    }
}

/// Verarbeitet eine Meeting-Aufnahme nach dem Meeting: Sprecher erkennen → Redebeiträge (Turns)
/// bilden → jeden Turn einzeln mit der normalen Diktier-Engine transkribieren.
///
/// „Turn-Slicing“ statt Wort-Alignment, weil es engine-unabhängig ist: Cohere liefert keine
/// Zeitstempel, Parakeet schon – so funktioniert beides über das unveränderte `TranscriptionEngine`-Protokoll.
final class MeetingTranscriber {
    /// Segmente desselben Sprechers mit kleinerer Lücke werden zu einem Turn zusammengefasst.
    private let maxGapWithinTurn: TimeInterval = 1.0
    /// Kürzere Turns sind für ASR unbrauchbar (halbe Wörter) und werden dem Vorgänger zugeschlagen.
    private let minTurnDuration: TimeInterval = 0.5
    /// Etwas Vorlauf/Nachlauf, damit Wortanfänge nicht abgeschnitten werden.
    private let slicePadding: TimeInterval = 0.15

    private let diarizer: SpeakerDiarizer
    private let engine: TranscriptionEngine

    init(diarizer: SpeakerDiarizer, engine: TranscriptionEngine) {
        self.diarizer = diarizer
        self.engine = engine
    }

    /// - Parameter expectedSpeakers: bekannte Teilnehmerzahl, nil = automatisch erkennen.
    func run(audioURL: URL, recordedAt: Date, language: AppLanguage, expectedSpeakers: Int?,
             progress: @escaping @Sendable (MeetingProgress) -> Void) async throws -> MeetingTranscript {
        // Fortschrittsbudget: 0–10 % Modelle, 10–40 % Sprecher, 40–100 % Transkription.
        progress(MeetingProgress(step: "Sprechermodell wird geladen…", fraction: 0))
        try await diarizer.prepare { p in
            progress(MeetingProgress(step: "Sprechermodell wird geladen…", fraction: p * 0.1))
        }
        try Task.checkCancellation()

        progress(MeetingProgress(step: "Sprecher werden erkannt…", fraction: 0.1))
        let segments = try await diarizer.diarize(audioURL, expectedSpeakers: expectedSpeakers) { p in
            progress(MeetingProgress(step: "Sprecher werden erkannt…", fraction: 0.1 + p * 0.3))
        }
        try Task.checkCancellation()

        let turns = Self.buildTurns(from: segments, maxGap: maxGapWithinTurn, minDuration: minTurnDuration)
        guard !turns.isEmpty else { throw MeetingTranscriberError.noSpeech }
        Log.meeting.notice("diarization: \(segments.count) segments → \(turns.count) turns")

        guard let file = try? AVAudioFile(forReading: audioURL) else { throw MeetingTranscriberError.audioUnreadable }
        let totalSeconds = Double(file.length) / file.processingFormat.sampleRate

        var result: [MeetingTranscript.Turn] = []
        for (index, turn) in turns.enumerated() {
            try Task.checkCancellation()
            progress(MeetingProgress(step: "Transkribiere Beitrag \(index + 1) von \(turns.count)…",
                                     fraction: 0.4 + 0.6 * Double(index) / Double(turns.count)))

            let samples = try Self.readSlice(from: file, start: max(0, turn.start - slicePadding),
                                             end: min(totalSeconds, turn.end + slicePadding))
            guard !samples.isEmpty else { continue }
            let text = try await engine.transcribe(samples, language: language)
            guard !text.isEmpty else { continue }
            result.append(MeetingTranscript.Turn(speakerID: turn.speakerID, start: turn.start, end: turn.end, text: text))
        }
        guard !result.isEmpty else { throw MeetingTranscriberError.noSpeech }

        progress(MeetingProgress(step: "Fertig", fraction: 1))
        return MeetingTranscript(date: recordedAt, durationSeconds: totalSeconds, engine: engine.kind,
                                 language: language, turns: Self.renumberSpeakers(result))
    }

    // MARK: Turns

    /// Fasst aufeinanderfolgende Segmente desselben Sprechers zusammen und räumt Mini-Turns auf.
    static func buildTurns(from segments: [SpeakerSegment], maxGap: TimeInterval, minDuration: TimeInterval) -> [SpeakerSegment] {
        var merged: [SpeakerSegment] = []
        for segment in segments.sorted(by: { $0.start < $1.start }) {
            if let last = merged.last, last.speakerID == segment.speakerID, segment.start - last.end <= maxGap {
                merged[merged.count - 1] = SpeakerSegment(speakerID: last.speakerID, start: last.start, end: max(last.end, segment.end))
            } else {
                merged.append(segment)
            }
        }

        // Zu kurze Turns: an den Vorgänger hängen (Zwischenrufe wie „ja“, „mhm“) oder verwerfen.
        var cleaned: [SpeakerSegment] = []
        for turn in merged {
            if turn.end - turn.start >= minDuration {
                cleaned.append(turn)
            } else if let last = cleaned.last, turn.start - last.end <= maxGap {
                cleaned[cleaned.count - 1] = SpeakerSegment(speakerID: last.speakerID, start: last.start, end: max(last.end, turn.end))
            }
        }
        return cleaned
    }

    /// Sprecher nach erstem Auftreten als „Sprecher 1“, „Sprecher 2“ … benennen.
    private static func renumberSpeakers(_ turns: [MeetingTranscript.Turn]) -> [MeetingTranscript.Turn] {
        var mapping: [String: String] = [:]
        return turns.map { turn in
            let name = mapping[turn.speakerID] ?? {
                let n = "Sprecher \(mapping.count + 1)"
                mapping[turn.speakerID] = n
                return n
            }()
            return MeetingTranscript.Turn(speakerID: name, start: turn.start, end: turn.end, text: turn.text)
        }
    }

    // MARK: Audio

    /// Liest einen Ausschnitt der 16-kHz-Mono-Aufnahme.
    private static func readSlice(from file: AVAudioFile, start: TimeInterval, end: TimeInterval) throws -> [Float] {
        let rate = file.processingFormat.sampleRate
        let startFrame = AVAudioFramePosition(start * rate)
        let frameCount = AVAudioFrameCount(max(0, (end - start) * rate))
        guard frameCount > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frameCount) else {
            return []
        }
        file.framePosition = startFrame
        try file.read(into: buffer, frameCount: frameCount)
        guard let channel = buffer.floatChannelData?[0] else { return [] }
        return Array(UnsafeBufferPointer(start: channel, count: Int(buffer.frameLength)))
    }
}
