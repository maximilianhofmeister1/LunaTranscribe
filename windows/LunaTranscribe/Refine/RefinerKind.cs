using System;
using LunaTranscribe.Engine;

namespace LunaTranscribe.Refine;

/// <summary>
/// Sprachmodelle für die Diktat-Nachbearbeitung. Gleiche Rolle wie <c>EngineKind</c> für die ASR.
/// Die lokalen Modelle laufen unter Windows über llama.cpp (LLamaSharp) auf der CPU, als GGUF von Hugging Face;
/// Claude Haiku läuft in der Cloud über die lokale Claude CLI.
/// „Apple Intelligence“ aus der macOS-Version gibt es hier bewusst nicht (kein Systemmodell unter Windows).
/// </summary>
public enum RefinerKind
{
    Lfm2_1_2B,      // Standard: schnellstes brauchbares Modell
    Qwen3_1_7B,     // Bessere Qualität, etwas langsamer, gutes Englisch
    Qwen3_4B,       // Beste Qualität (Selbstkorrekturen), deutlich langsamer auf der CPU
    ClaudeHaiku,    // Claude Haiku über die lokale Claude CLI – Cloud, Text verlässt den PC
}

public static class RefinerKindExtensions
{
    // Richtwerte für ein Diktat mit 1–2 Sätzen auf einer 4-Kern-Laptop-CPU (Ryzen 5 3500U), Prompt-Anfang im Cache –
    // gemessen mit --refine-selftest (LFM2; Qwen3 hochgerechnet). Schnellere CPUs brauchen entsprechend weniger.
    public static string Label(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B => "Qwen3 1.7B (genauer – ~4–8 s pro Diktat, ~1,1 GB Download, ~1,5 GB RAM)",
        RefinerKind.Qwen3_4B => "Qwen3 4B (beste Qualität – ~10–20 s pro Diktat, ~2,5 GB Download, ~3 GB RAM)",
        RefinerKind.ClaudeHaiku => "Claude Haiku 5.5 (Cloud über die Claude CLI – ~3–5 s pro Diktat, kein Download, kein RAM)",
        _ => "LFM2 1.2B (empfohlen – ~2–4 s pro Diktat, ~0,7 GB Download, ~0,8 GB RAM)",
    };

    public static string ShortLabel(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B => "Qwen3 1.7B",
        RefinerKind.Qwen3_4B => "Qwen3 4B",
        RefinerKind.ClaudeHaiku => "Claude Haiku",
        _ => "LFM2 1.2B",
    };

    /// <summary>Erklärung für die Einstellungen (Stärken/Schwächen wie in der README-Tabelle der macOS-Version).</summary>
    public static string Description(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B =>
            "Konservativ: lässt Selbstkorrekturen lieber stehen („um zehn, nein um elf Uhr“); gutes Englisch.",
        RefinerKind.Qwen3_4B =>
            "Löst Selbstkorrekturen innerhalb eines Satzes („am Montag nein am Dienstag“ → „am Dienstag“). Braucht spürbar mehr Zeit und RAM.",
        RefinerKind.ClaudeHaiku =>
            "Beste Qualität ohne Last auf dem PC, unabhängig vom Prozessor. Braucht Internet und eine angemeldete Claude CLI.",
        _ => "Schnellstes brauchbares Modell; verschmilzt Selbstkorrekturen gern falsch („um zehn Uhr und elf Uhr“).",
    };

    /// <summary>Schlüssel für Einstellungen und <c>--model</c> (wie die rawValues der macOS-Version).</summary>
    public static string Key(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B => "qwen3_1_7b",
        RefinerKind.Qwen3_4B => "qwen3_4b",
        RefinerKind.ClaudeHaiku => "claudeHaiku",
        _ => "lfm2_1_2b",
    };

    /// <summary>Unbekannte Schlüssel (auch „appleIntelligence“ aus macOS) fallen auf den Standard zurück.</summary>
    public static RefinerKind FromKey(string? key) => key?.ToLowerInvariant() switch
    {
        "qwen3_1_7b" or "qwen3-1.7b" => RefinerKind.Qwen3_1_7B,
        "qwen3_4b" or "qwen3-4b" => RefinerKind.Qwen3_4B,
        "claudehaiku" or "claude" or "haiku" => RefinerKind.ClaudeHaiku,
        _ => RefinerKind.Lfm2_1_2B,
    };

    public static bool TryParseKey(string? key, out RefinerKind kind)
    {
        kind = FromKey(key);
        return key?.ToLowerInvariant() is "lfm2" or "lfm2_1_2b" or "lfm2-1.2b"
            or "qwen3_1_7b" or "qwen3-1.7b" or "qwen3_4b" or "qwen3-4b" or "claudehaiku" or "claude" or "haiku";
    }

    /// <summary>Läuft in der Cloud (Claude CLI) statt lokal über llama.cpp – kein GGUF, kein Download, kein RAM.</summary>
    public static bool IsCloud(this RefinerKind k) => k == RefinerKind.ClaudeHaiku;

    /// <summary>
    /// GGUF auf Hugging Face (Q4_K_M ≈ 4-bit-MLX der macOS-Version). LFM2 und Qwen3 4B aus den offiziellen Repos;
    /// Qwen3 1.7B gibt es offiziell nur als Q8_0 (1,8 GB), daher die Q4_K_M-Fassung von Unsloth.
    /// Nur für lokale Modelle (<see cref="IsCloud"/> = false).
    /// </summary>
    public static ModelSpec Model(this RefinerKind k) => k switch
    {
        RefinerKind.ClaudeHaiku => throw new InvalidOperationException("Claude Haiku hat keine Modelldatei."),
        RefinerKind.Qwen3_1_7B => new("qwen3-1.7b-q4_k_m", "unsloth/Qwen3-1.7B-GGUF", new[] { "Qwen3-1.7B-Q4_K_M.gguf" }),
        RefinerKind.Qwen3_4B => new("qwen3-4b-q4_k_m", "Qwen/Qwen3-4B-GGUF", new[] { "Qwen3-4B-Q4_K_M.gguf" }),
        _ => new("lfm2-1.2b-q4_k_m", "LiquidAI/LFM2-1.2B-GGUF", new[] { "LFM2-1.2B-Q4_K_M.gguf" }),
    };

    /// <summary>Pfad der GGUF-Datei unter %LOCALAPPDATA%\LunaTranscribe\Models.</summary>
    public static string ModelFile(this RefinerKind k)
    {
        var spec = k.Model();
        return spec.PathOf(spec.Files[0]);
    }

    /// <summary>Downloadgröße in Bytes (laut Hugging Face).</summary>
    public static long DownloadBytes(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B => 1_107_409_472,
        RefinerKind.Qwen3_4B => 2_497_280_256,
        RefinerKind.ClaudeHaiku => 0,
        _ => 730_893_248,
    };

    /// <summary>Kontextfenster in Tokens. Begrenzt den KV-Cache (Qwen3 4B: ~144 KB pro Token) – das 20-s-Timeout
    /// begrenzt die Diktatlänge auf der CPU ohnehin früher.</summary>
    public static uint ContextSize(this RefinerKind k) => k switch
    {
        RefinerKind.Qwen3_1_7B => 2048,
        RefinerKind.Qwen3_4B => 2048,
        _ => 4096,
    };

    /// <summary>Qwen3 ist ein Hybrid-Modell mit Reasoning-Block; der wird per leerem „&lt;think&gt;“-Block übersprungen
    /// (entspricht <c>enable_thinking=false</c> im offiziellen Chat-Template).</summary>
    public static bool HasThinkingMode(this RefinerKind k) => k is RefinerKind.Qwen3_1_7B or RefinerKind.Qwen3_4B;
}
