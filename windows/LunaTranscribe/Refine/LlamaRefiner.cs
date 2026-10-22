using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LunaCore;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;

namespace LunaTranscribe.Refine;

/// <summary>
/// Nachbearbeitung mit einem quantisierten Open-Weights-Modell (LFM2, Qwen3) über llama.cpp (LLamaSharp) auf der CPU.
/// Greedy, ohne Thinking, Chat-Template des Modells. Jedes Diktat ist zustandslos; nur der immer gleiche
/// Prompt-Anfang (Anweisung + Beispielpaare) wird als KV-Zustand zwischengespeichert, damit warme Aufrufe
/// nicht jedes Mal ~500 Tokens neu rechnen. Die GGUF-Datei liegt wie die ASR-Modelle unter <see cref="ModelPaths.Base"/>.
/// </summary>
public sealed class LlamaRefiner : ITextRefiner
{
    /// <summary>Harte Obergrenze pro Diktat; danach wird der Rohtext eingefügt, damit nichts hängen bleibt.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Obergrenze für die Antwortlänge, damit ein Modell nicht endlos weiterschreibt.</summary>
    private const int MaxOutputTokens = 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);   // ein llama-Kontext, nie parallel benutzen
    private volatile Session? _session;

    static LlamaRefiner()
    {
        // llama.cpp schreibt sonst jede Ladezeile auf stderr; nur Fehler ins Log (nie Texte).
        try
        {
            NativeLibraryConfig.All.WithLogCallback((level, message) =>
            {
                if (level == LLamaLogLevel.Error) Log.Write("llama: " + message.TrimEnd());
            });
        }
        catch (Exception ex) { Log.Write("llama log config: " + ex.Message); }
    }

    public LlamaRefiner(RefinerKind kind) => Kind = kind;

    public RefinerKind Kind { get; }

    public bool IsLoaded => _session != null;

    public bool FilesPresent => Kind.Model().FilesPresent;

    /// <summary>Kennzahlen des letzten Aufrufs (für Selbsttest und Log), ohne Text.</summary>
    public string? LastStats { get; private set; }

    public async Task PrepareAsync(Action<double> progress, RefineWarmup? warmup, CancellationToken ct)
    {
        if (_session != null) return;

        var spec = Kind.Model();
        if (!spec.FilesPresent)
            await ModelDownloader.EnsureAsync(spec, p => progress(p * 0.9), ct);
        progress(0.9);
        ct.ThrowIfCancellationRequested();

        var session = await Task.Run(() => Session.Load(Kind), ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            progress(0.95);

            // Warm-up: Gewichte in den Speicher holen und Threads starten. Mit Sprache/Anweisung wird gleich der
            // Prompt-Anfang vorberechnet – dann ist schon das erste Diktat so schnell wie alle weiteren.
            await WarmupAsync(session, warmup, ct);

            ct.ThrowIfCancellationRequested();
            _session = session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        progress(1.0);
    }

    public async Task PrimeAsync(RefineWarmup warmup, CancellationToken ct)
    {
        if (_session is { } session) await WarmupAsync(session, warmup, ct);
    }

    /// <summary>Ein Mini-Durchlauf unter dem Gate; mit <paramref name="warmup"/> landet der Prompt-Anfang im Cache.</summary>
    private async Task WarmupAsync(Session session, RefineWarmup? warmup, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(ct);
        try
        {
            if (session.IsDisposed) return;
            var messages = warmup != null
                ? RefinePrompt.Build("ok", warmup.Language, warmup.PromptTemplate)
                : new[] { new RefinePrompt.Message(RefinePrompt.Role.System, "Repeat the text."), new RefinePrompt.Message(RefinePrompt.Role.User, "ok") };
            await Task.Run(() => session.Generate(messages, 1, ct), ct);
            Log.Write($"refine {Kind.Key()}: warm-up{(warmup != null ? " " + warmup.Language.Code() : "")} in {sw.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Write("refine warm-up failed: " + ex.Message); /* nicht kritisch */ }
        finally { _gate.Release(); }
    }

    public async Task<string> RefineAsync(string text, AppLanguage language, string? promptTemplate, CancellationToken ct)
    {
        if (_session == null) throw RefinerException.NotLoaded();
        var messages = RefinePrompt.Build(text, language, promptTemplate);
        var budget = OutputBudget(text);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;

        // Gate und Generierung laufen gemeinsam im Hintergrund: Läuft das Timeout ab, kehrt der Aufrufer sofort zurück;
        // die Generierung bricht beim nächsten Token ab und gibt erst dann den Kontext frei.
        var work = Task.Run(async () =>
        {
            await _gate.WaitAsync(token);
            try
            {
                var session = _session ?? throw RefinerException.NotLoaded();
                var raw = session.Generate(messages, budget, token);
                LastStats = session.LastStats;
                return raw;
            }
            finally { _gate.Release(); }
        }, token);
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        string output;
        try
        {
            output = await work.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw RefinerException.Timeout();
        }
        catch (RefinerException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw RefinerException.Failed("Nachbearbeitung fehlgeschlagen: " + ex.Message, ex);
        }

        var cleaned = RefinePrompt.Sanitize(output, text);
        if (cleaned == null)
        {
            Log.Write($"refine {Kind.Key()}: implausible output ({output.Length} chars for {text.Length})");
            throw RefinerException.ImplausibleOutput();
        }
        return cleaned;
    }

    /// <summary>
    /// Erlaubt bis zu doppelt so viele Tokens wie die Eingabe (Interpunktion, Großschreibung) plus etwas Reserve;
    /// grob 1 Token ≈ 3 Zeichen für deutsche Texte.
    /// </summary>
    private static int OutputBudget(string text) => Math.Min(MaxOutputTokens, text.Length * 2 / 3 + 64);

    /// <summary>Gibt das Modell frei. Läuft gerade eine Generierung, geschieht das direkt nach deren Ende.</summary>
    public void Unload()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session == null) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { session.Dispose(); }
            finally { _gate.Release(); }
        });
    }

    public void Dispose() => Unload();

    /// <summary>Geladene Gewichte + Kontext + zwischengespeicherter Prompt-Anfang. Nur unter dem Gate benutzen.</summary>
    private sealed class Session : IDisposable
    {
        private readonly RefinerKind _kind;
        private readonly LLamaWeights _weights;
        private readonly LLamaContext _context;
        private readonly LLamaBatch _batch = new();
        private LLamaToken[]? _prefixTokens;
        private LLamaContext.SequenceState? _prefixState;

        public string? LastStats { get; private set; }

        public bool IsDisposed { get; private set; }

        private Session(RefinerKind kind, LLamaWeights weights, LLamaContext context)
        {
            _kind = kind;
            _weights = weights;
            _context = context;
        }

        public static Session Load(RefinerKind kind)
        {
            var sw = Stopwatch.StartNew();
            var p = new ModelParams(kind.ModelFile())
            {
                ContextSize = kind.ContextSize(),
                GpuLayerCount = 0,
                // ≈ physische Kerne. Mehr Threads (Hyperthreading) brachten im Test nichts bzw. bremsten die Generierung.
                Threads = ModelPaths.NumThreads,
                BatchThreads = ModelPaths.NumThreads,
                BatchSize = 512,
                UBatchSize = 512,
            };
            var weights = LLamaWeights.LoadFromFile(p);
            try
            {
                var context = weights.CreateContext(p);
                Log.Write($"refine {kind.Key()}: model loaded in {sw.Elapsed.TotalSeconds:0.0}s (ctx {context.ContextSize}, {ModelPaths.NumThreads} threads)");
                return new Session(kind, weights, context);
            }
            catch
            {
                weights.Dispose();
                throw;
            }
        }

        /// <summary>Ein zustandsloser Chat-Durchlauf: greedy, ohne Thinking, höchstens <paramref name="maxTokens"/> Tokens.</summary>
        public string Generate(IReadOnlyList<RefinePrompt.Message> messages, int maxTokens, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var full = Render(messages, addAssistant: true);
            // Qwen3: leerer Reasoning-Block wie im offiziellen Template mit enable_thinking=false.
            if (_kind.HasThinkingMode()) full += "<think>\n\n</think>\n\n";
            var tokens = _context.Tokenize(full, addBos: true, special: true);

            var nCtx = (int)_context.ContextSize;
            if (tokens.Length + 16 > nCtx)
                throw RefinerException.Failed("Das Diktat ist zu lang für die Nachbearbeitung.");
            maxTokens = Math.Min(maxTokens, nCtx - tokens.Length);

            // Prompt-Anfang (alles vor dem Diktat) ist bei gleicher Sprache/Anweisung immer identisch.
            var prefixText = Render(messages.Take(messages.Count - 1).ToList(), addAssistant: false);
            LLamaToken[]? prefix = null;
            if (full.StartsWith(prefixText, StringComparison.Ordinal))
            {
                var candidate = _context.Tokenize(prefixText, addBos: true, special: true);
                if (candidate.Length > 0 && candidate.Length < tokens.Length && tokens.AsSpan(0, candidate.Length).SequenceEqual(candidate))
                    prefix = candidate;
            }

            var prepMs = sw.Elapsed.TotalMilliseconds;
            var handle = _context.NativeHandle;
            handle.MemoryClear(true);
            var nPast = 0;
            var cached = 0;
            var restoreMs = 0.0;
            if (prefix != null && _prefixState != null && _prefixTokens != null && _prefixTokens.AsSpan().SequenceEqual(prefix))
            {
                _context.LoadState(_prefixState, LLamaSeqId.Zero);
                nPast = cached = prefix.Length;
                restoreMs = sw.Elapsed.TotalMilliseconds - prepMs;
            }
            else if (prefix != null)
            {
                DecodePrompt(prefix, 0, ct);
                nPast = prefix.Length;
                _prefixState?.Dispose();
                _prefixState = _context.GetState(LLamaSeqId.Zero);
                _prefixTokens = prefix;
            }

            var logitIndex = DecodePrompt(tokens.AsSpan(nPast).ToArray(), nPast, ct);
            nPast = tokens.Length;
            var promptMs = sw.Elapsed.TotalMilliseconds;

            var vocab = _weights.Vocab;
            var decoder = new StreamingTokenDecoder(_context);
            var generated = 0;
            var genWatch = Stopwatch.StartNew();
            for (; generated < maxTokens; generated++)
            {
                ct.ThrowIfCancellationRequested();
                var next = ArgMax(handle.GetLogitsIth(logitIndex));
                if (next.IsEndOfGeneration(vocab)) break;
                decoder.Add(next);

                _batch.Clear();
                _batch.Add(next, nPast++, LLamaSeqId.Zero, true);
                Check(handle.Decode(_batch));
                logitIndex = 0;
            }
            var genSeconds = genWatch.Elapsed.TotalSeconds;

            LastStats = $"Prompt {tokens.Length} Tokens ({cached} aus Cache) in {promptMs:0} ms " +
                        $"(Template {prepMs:0} ms, Cache {restoreMs:0} ms), " +
                        $"{generated} Tokens in {genSeconds * 1000:0} ms ({(genSeconds > 0 ? generated / genSeconds : 0):0.0} Tokens/s)";
            Log.Write($"refine {_kind.Key()}: prompt {tokens.Length} tokens ({cached} cached) {promptMs:0}ms, " +
                      $"generated {generated} tokens {genSeconds * 1000:0}ms");
            return decoder.Read();
        }

        /// <summary>Rechnet Prompt-Tokens in Batch-Häppchen; Logits nur für das letzte Token. Gibt dessen Logit-Index zurück.</summary>
        private int DecodePrompt(LLamaToken[] tokens, int startPos, CancellationToken ct)
        {
            var chunk = (int)_context.BatchSize;
            var lastIndex = 0;
            for (var offset = 0; offset < tokens.Length; offset += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(chunk, tokens.Length - offset);
                _batch.Clear();
                for (var i = 0; i < count; i++)
                {
                    var isLast = offset + i == tokens.Length - 1;
                    _batch.Add(tokens[offset + i], startPos + offset + i, LLamaSeqId.Zero, isLast);
                }
                Check(_context.NativeHandle.Decode(_batch));
                lastIndex = count - 1;
            }
            return lastIndex;
        }

        private static void Check(DecodeResult result)
        {
            if (result != DecodeResult.Ok) throw RefinerException.Failed($"llama.cpp-Fehler beim Rechnen ({result}).");
        }

        private static LLamaToken ArgMax(Span<float> logits)
        {
            var best = 0;
            var bestValue = float.NegativeInfinity;
            for (var i = 0; i < logits.Length; i++)
            {
                if (logits[i] > bestValue) { bestValue = logits[i]; best = i; }
            }
            return best;
        }

        /// <summary>Chat-Template aus den GGUF-Metadaten des Modells (llama.cpp erkennt LFM2 und ChatML/Qwen3).</summary>
        private string Render(IReadOnlyList<RefinePrompt.Message> messages, bool addAssistant)
        {
            var template = new LLamaTemplate(_weights, strict: true) { AddAssistant = addAssistant };
            foreach (var m in messages) template.Add(m.RoleName, m.Content);
            return Encoding.UTF8.GetString(template.Apply());
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _prefixState?.Dispose();
            _context.Dispose();
            _weights.Dispose();
        }
    }
}
