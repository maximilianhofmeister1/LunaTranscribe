using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using Microsoft.ML.OnnxRuntime;

namespace LunaMCP.Embedding;

/// <summary>
/// Embedding-Modell für die inhaltliche Suche: multilingual-e5-small als ONNX auf der CPU (ONNX Runtime). Lädt nur aus
/// dem lokalen Modellordner (<see cref="EmbeddingModelKinds.IsDownloaded"/>) – luna-mcp lädt im Serverbetrieb nie etwas
/// herunter.
///
/// Details, die leicht übersehen werden:
/// <list type="bullet">
/// <item>E5 erwartet die Präfixe „query: “ / „passage: “.</item>
/// <item>Aufgefüllt wird mit &lt;pad&gt;; die Positionen rechnet der ONNX-Graph selbst.</item>
/// <item>Mittelwert über die echten Token (Maske), dann L2-Normalisierung – wie sentence-transformers.</item>
/// </list>
/// Nach <see cref="IdleUnload"/> ohne Anfrage wird das Modell entladen (~500 MB Arbeitsspeicher).
/// </summary>
public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    /// <summary>Stapelgröße beim Indexieren; klein genug für wenig Speicher.</summary>
    public const int BatchSize = 16;

    public static TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(10);

    private readonly EmbeddingModelKind _kind;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private XlmRobertaTokenizer? _tokenizer;
    private readonly Timer _idleTimer;
    private DateTime _lastUse = DateTime.UtcNow;

    public OnnxTextEmbedder(EmbeddingModelKind kind)
    {
        _kind = kind;
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public string ModelID => _kind.ModelID();

    public bool IsLoaded
    {
        get { lock (_gate) return _session is not null; }
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var result = new List<float[]>(texts.Count);
            for (var start = 0; start < texts.Count; start += BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = texts.Skip(start).Take(BatchSize).Select(t => _kind.DocumentPrefix() + t).ToList();
                result.AddRange(Embed(batch));
            }
            return result.ToArray();
        }, cancellationToken);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        Task.Run(() => Embed(new[] { _kind.QueryPrefix() + text })[0], cancellationToken);

    /// <summary>Lädt Tokenizer und Modell (einmalig, ~1–2 s).</summary>
    public void Load()
    {
        lock (_gate) EnsureLoaded();
    }

    public void Unload()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _tokenizer = null;
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        Unload();
    }

    private void UnloadIfIdle()
    {
        lock (_gate)
        {
            if (_session is null || DateTime.UtcNow - _lastUse < IdleUnload) return;
            _session.Dispose();
            _session = null;
            _tokenizer = null;
            McpLog.Write("Embedding-Modell nach Inaktivität entladen");
        }
    }

    private void EnsureLoaded()
    {
        if (_session is not null && _tokenizer is not null) return;
        if (!_kind.IsDownloaded())
            throw new InvalidOperationException("Das Modell für die inhaltliche Suche ist noch nicht geladen (LunaTranscribe → Einstellungen → Datenschutz).");
        var started = DateTime.UtcNow;
        _tokenizer = XlmRobertaTokenizer.Load(_kind.LocalPath("tokenizer.json"));
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
        };
        _session = new InferenceSession(_kind.LocalPath("model.onnx"), options);
        McpLog.Write($"Embedding-Modell geladen in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
    }

    private float[][] Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        lock (_gate)
        {
            EnsureLoaded();
            _lastUse = DateTime.UtcNow;
            var session = _session!;
            var tokens = texts.Select(t => _tokenizer!.Encode(t, _kind.MaxTokens())).ToList();
            var batch = tokens.Count;
            var length = tokens.Max(t => t.Length);

            var ids = new long[batch * length];
            var mask = new long[batch * length];
            var types = new long[batch * length];
            for (var b = 0; b < batch; b++)
            {
                for (var i = 0; i < length; i++)
                {
                    var real = i < tokens[b].Length;
                    ids[b * length + i] = real ? tokens[b][i] : _tokenizer!.PadId;
                    mask[b * length + i] = real ? 1 : 0;
                }
            }

            var shape = new long[] { batch, length };
            var names = new List<string>();
            var values = new List<OrtValue>();
            try
            {
                foreach (var name in session.InputMetadata.Keys)
                {
                    var data = name switch
                    {
                        "input_ids" => ids,
                        "attention_mask" => mask,
                        "token_type_ids" => types,
                        _ => throw new InvalidOperationException($"Unerwarteter Modell-Eingang „{name}“."),
                    };
                    names.Add(name);
                    values.Add(OrtValue.CreateTensorValueFromMemory(data, shape));
                }
                var outputName = session.OutputMetadata.Keys.Contains("last_hidden_state")
                    ? "last_hidden_state"
                    : session.OutputMetadata.Keys.First();
                using var runOptions = new RunOptions();
                using var outputs = session.Run(runOptions, names, values, new[] { outputName });
                var output = outputs[0];
                var outShape = output.GetTensorTypeAndShape().Shape;
                if (outShape.Length != 3) throw new InvalidOperationException("Unerwartete Ausgabeform des Embedding-Modells.");
                var dimensions = (int)outShape[2];
                var hidden = output.GetTensorDataAsSpan<float>();

                // Mittelwert über die echten Token, dann L2-Normalisierung.
                var result = new float[batch][];
                for (var b = 0; b < batch; b++)
                {
                    var pooled = new float[dimensions];
                    var count = 0;
                    for (var i = 0; i < length; i++)
                    {
                        if (mask[b * length + i] == 0) continue;
                        count++;
                        var offset = (b * length + i) * dimensions;
                        for (var d = 0; d < dimensions; d++) pooled[d] += hidden[offset + d];
                    }
                    if (count > 0)
                        for (var d = 0; d < dimensions; d++) pooled[d] /= count;
                    result[b] = VectorMath.Normalized(pooled);
                }
                return result;
            }
            finally
            {
                foreach (var value in values) value.Dispose();
            }
        }
    }
}
