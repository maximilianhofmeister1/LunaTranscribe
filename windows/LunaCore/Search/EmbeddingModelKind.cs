using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LunaCore;

/// <summary>
/// Die Embedding-Modelle für die inhaltliche Suche. Nur Daten – geladen und gerechnet wird in luna-mcp
/// (ONNX Runtime auf der CPU). Die App lädt nichts selbst, sondern startet <c>luna-mcp --download-embedding-model</c>.
/// </summary>
public enum EmbeddingModelKind
{
    /// <summary>intfloat/multilingual-e5-small: MIT, ~118 M Parameter, 384 Dimensionen, rund 100 Sprachen.</summary>
    MultilingualE5Small,
}

/// <summary>Eine Datei des Modells auf Hugging Face.</summary>
/// <param name="RemotePath">Pfad im Repo (bei festem Commit).</param>
/// <param name="LocalName">Dateiname im lokalen Modellordner.</param>
/// <param name="Sha256">Erwartete Prüfsumme (Git-LFS-OID) oder null für kleine Textdateien.</param>
/// <param name="Size">Erwartete Größe in Bytes (für den Fortschritt).</param>
public sealed record EmbeddingModelFile(string RemotePath, string LocalName, string? Sha256, long Size);

public static class EmbeddingModelKinds
{
    public static EmbeddingModelKind Standard => EmbeddingModelKind.MultilingualE5Small;

    /// <summary>Name der Datei, die einen vollständigen, geprüften Download markiert.</summary>
    public const string CompleteMarker = ".complete";

    public static string HuggingFaceID(this EmbeddingModelKind kind) => "intfloat/multilingual-e5-small";

    /// <summary>Fester Commit, damit Prüfsummen und Tokenizer zusammenpassen.</summary>
    public static string Revision(this EmbeddingModelKind kind) => "614241f622f53c4eeff9890bdc4f31cfecc418b3";

    /// <summary>Kennung der Vektoren im Index; ändert sie sich, werden alle Vektoren neu berechnet.</summary>
    public static string ModelID(this EmbeddingModelKind kind) => kind.HuggingFaceID();

    public static string Label(this EmbeddingModelKind kind) => "Multilingual E5 Small (~470 MB)";

    /// <summary>E5 unterscheidet Anfrage und Dokument über ein Präfix (so trainiert).</summary>
    public static string QueryPrefix(this EmbeddingModelKind kind) => "query: ";
    public static string DocumentPrefix(this EmbeddingModelKind kind) => "passage: ";

    /// <summary>Längere Eingaben werden abgeschnitten (Positions-Embeddings des Modells).</summary>
    public static int MaxTokens(this EmbeddingModelKind kind) => 512;

    /// <summary>Die offizielle ONNX-Fassung (FP32) aus dem Repo von intfloat samt Tokenizer.</summary>
    public static IReadOnlyList<EmbeddingModelFile> Files(this EmbeddingModelKind kind) => new[]
    {
        new EmbeddingModelFile("onnx/model.onnx", "model.onnx", "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665", 470_268_510),
        new EmbeddingModelFile("onnx/tokenizer.json", "tokenizer.json", "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39", 17_082_730),
        new EmbeddingModelFile("onnx/config.json", "config.json", null, 653),
    };

    public static string DownloadUrl(this EmbeddingModelKind kind, EmbeddingModelFile file) =>
        $"https://huggingface.co/{kind.HuggingFaceID()}/resolve/{kind.Revision()}/{file.RemotePath}";

    /// <summary>Ablage nach Hugging-Face-Konvention unter <see cref="AppPaths.Models"/>.</summary>
    public static string LocalFolder(this EmbeddingModelKind kind) =>
        Path.Combine(new[] { AppPaths.Models, "models" }.Concat(kind.HuggingFaceID().Split('/')).ToArray());

    public static string LocalPath(this EmbeddingModelKind kind, string localName) => Path.Combine(kind.LocalFolder(), localName);

    /// <summary>Liegt das Modell vollständig und geprüft lokal? luna-mcp lädt nie im Serverbetrieb herunter.</summary>
    public static bool IsDownloaded(this EmbeddingModelKind kind) =>
        File.Exists(kind.LocalPath(CompleteMarker)) && kind.Files().All(f => File.Exists(kind.LocalPath(f.LocalName)));
}
