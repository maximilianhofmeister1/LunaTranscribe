import Foundation

/// Ein Eintrag im eigenen Wörterbuch: die richtige Schreibweise und die Varianten,
/// als die das ASR-Modell den Begriff falsch erkennt.
public struct VocabularyEntry: Codable, Identifiable, Equatable, Sendable {
    public var id: UUID
    /// Richtige Schreibweise, landet so im Text („LunaTranscribe“).
    public var term: String
    /// Bekannte Fehlerkennungen („Luna Transcribe“, „Luna transkribe“).
    public var aliases: [String]

    public init(id: UUID = UUID(), term: String, aliases: [String] = []) {
        self.id = id
        self.term = term
        self.aliases = aliases
    }
}
