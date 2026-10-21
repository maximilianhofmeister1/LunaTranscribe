using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

/// <summary>
/// Ein Eintrag im eigenen Wörterbuch: die richtige Schreibweise und die Varianten, als die das ASR-Modell
/// den Begriff falsch erkennt. Ohne JSON-Attribute: Die App speichert ihn in settings.json mit
/// PascalCase-Namen (<c>Id</c>, <c>Term</c>, <c>Aliases</c>).
/// </summary>
public sealed class VocabularyEntry : IEquatable<VocabularyEntry>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Richtige Schreibweise, landet so im Text („LunaTranscribe“).</summary>
    public string Term { get; set; } = "";
    /// <summary>Bekannte Fehlerkennungen („Luna Transcribe“, „Luna transkribe“).</summary>
    public List<string> Aliases { get; set; } = new();

    public VocabularyEntry() { }

    public VocabularyEntry(string term, IEnumerable<string>? aliases = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Term = term;
        Aliases = aliases?.ToList() ?? new();
    }

    public VocabularyEntry Clone() => new(Term, Aliases, Id);

    public bool Equals(VocabularyEntry? other) =>
        other is not null && Id == other.Id && Term == other.Term && Aliases.SequenceEqual(other.Aliases);

    public override bool Equals(object? obj) => Equals(obj as VocabularyEntry);

    public override int GetHashCode() => HashCode.Combine(Id, Term);

    public override string ToString() => Aliases.Count == 0 ? Term : $"{Term} ← {string.Join(", ", Aliases)}";
}
