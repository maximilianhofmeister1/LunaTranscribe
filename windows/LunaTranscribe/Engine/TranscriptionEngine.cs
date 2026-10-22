using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Engine;

/// <summary>
/// Gemeinsame Schnittstelle. Weitere Engines (z. B. Whisper) lassen sich ergänzen,
/// ohne dass sich UI oder Aufnahme-Logik ändern.
/// </summary>
public interface ITranscriptionEngine : IDisposable
{
    EngineKind Kind { get; }
    bool IsLoaded { get; }

    /// <summary>true, wenn die Modelldateien lokal liegen – Laden dauert dann nur Sekunden, kein Download.</summary>
    bool FilesPresent { get; }

    /// <summary>
    /// Lädt das Modell (beim ersten Mal von Hugging Face herunter) und wärmt es auf.
    /// <paramref name="progress"/> liegt zwischen 0 und 1 (0–0.9 Download, 0.9–0.95 Laden, 0.95–1 Warm-up).
    /// </summary>
    Task PrepareAsync(Action<double> progress, CancellationToken ct);

    /// <summary>16 kHz, mono, Float32. Gibt den erkannten Text zurück (getrimmt).</summary>
    Task<string> TranscribeAsync(float[] samples, AppLanguage language, CancellationToken ct);

    void Unload();
}

public sealed class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
    public EngineException(string message, Exception inner) : base(message, inner) { }

    public static EngineException NotLoaded() => new("Das Modell ist noch nicht geladen.");

    public static EngineException UnsupportedLanguage(AppLanguage l, EngineKind e) =>
        new($"{l.DisplayName()} wird von {e.ShortLabel()} nicht unterstützt.");
}

/// <summary>Basisordner für alle Modelle: %LOCALAPPDATA%\LunaTranscribe\Models</summary>
public static class ModelPaths
{
    public static string Base =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaTranscribe", "Models");

    /// <summary>Anzahl Threads für ONNX Runtime: halbe logische Kerne (≈ physische Kerne), 1–8.</summary>
    public static int NumThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
}
