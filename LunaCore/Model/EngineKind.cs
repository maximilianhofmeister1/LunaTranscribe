namespace LunaCore;

/// <summary>
/// Die Spracherkennungs-Engines. Die Implementierungen (sherpa-onnx) liegen in der App; hier steht nur,
/// was auch in gespeicherten Meetings vorkommt. Gespeichert wird der Schlüssel („parakeet“/„cohere“).
/// </summary>
public enum EngineKind
{
    Parakeet,   // Standard: schnell, ~1 GB RAM
    Cohere,     // Beste Qualität, aber ~3 GB Modell und ~4 GB RAM
}

public static class EngineKindExtensions
{
    public static string Label(this EngineKind k) => k switch
    {
        EngineKind.Cohere => "Cohere Transcribe (beste Qualität, 14 Sprachen, ~3 GB Download, ~4 GB RAM)",
        EngineKind.Parakeet => "Parakeet TDT v3 (empfohlen – schnell, 25 Sprachen, ~0,7 GB Download, ~1 GB RAM)",
        _ => k.ToString(),
    };

    public static string ShortLabel(this EngineKind k) => k switch
    {
        EngineKind.Cohere => "Cohere Transcribe",
        EngineKind.Parakeet => "Parakeet v3",
        _ => k.ToString(),
    };

    public static string Key(this EngineKind k) => k == EngineKind.Cohere ? "cohere" : "parakeet";

    public static EngineKind FromKey(string? key) => key == "cohere" ? EngineKind.Cohere : EngineKind.Parakeet;
}
