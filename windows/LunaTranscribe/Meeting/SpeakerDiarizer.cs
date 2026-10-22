using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaTranscribe.Audio;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;
using SherpaOnnx;

namespace LunaTranscribe.Meeting;

/// <summary>Ein zusammenhängender Abschnitt, in dem ein Sprecher spricht (Sekunden ab Aufnahmestart).</summary>
public sealed record SpeakerSegment(string SpeakerId, double Start, double End);

/// <summary>
/// Sprecher-Erkennung (Diarization), komplett lokal über sherpa-onnx, in zwei Stufen:
///
/// 1. <see cref="OfflineSpeakerDiarization"/> mit pyannote segmentation 3.0: findet Sprachabschnitte und
///    Sprecherwechsel (framegenau, auch ohne Pause).
/// 2. Nachclustern: Jeder Abschnitt wird in ≈ 1,5-s-Fenster geteilt, je Fenster ein Stimm-Embedding
///    (3D-Speaker CAM++, zh/en-trainiert, sprachunabhängig) berechnet und global per Average-Linkage
///    geclustert. Nötig, weil die Segmentierung ähnliche Stimmen innerhalb ihrer 10-s-Fenster gern
///    zusammenwirft – dann mischt sherpa die Embeddings und erkennt nur noch einen Sprecher.
///
/// Rein akustisch, unabhängig davon, ob eine Stimme über das Mikrofon oder das Systemaudio kam. Die Modelle
/// (≈ 34 MB) liegen wie die ASR-Modelle unter <see cref="ModelPaths.Base"/>, werden beim ersten Meeting
/// geladen und nach der Verarbeitung wieder entladen.
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    public static readonly ModelSpec SegmentationModel = new(
        FolderName: "pyannote-segmentation-3-0",
        HuggingFaceRepo: "csukuangfj/sherpa-onnx-pyannote-segmentation-3-0",
        Files: new[] { "model.onnx" });

    public static readonly ModelSpec EmbeddingModel = new(
        FolderName: "speaker-embedding-campplus",
        HuggingFaceRepo: "csukuangfj/speaker-embedding-models",
        Files: new[] { "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx" });

    // Stufe 1 (sherpa)
    /// <summary>Vorschub des 10-s-Segmentierungsfensters relativ zur Fensterlänge (sherpa-Standard 0,1 ist ≈ 5× langsamer).</summary>
    private const float WindowShiftRatio = 0.5f;
    /// <summary>Kürzere Sprechabschnitte verwerfen bzw. kürzere Pausen eines Sprechers überbrücken (Sekunden).</summary>
    private const float MinDurationOn = 0.3f;
    private const float MinDurationOff = 0.5f;

    // Stufe 2 (Nachclustern)
    /// <summary>Ziel-Fensterlänge für ein Embedding: kürzer = genauere Wechsel, länger = stabilere Stimmerkennung.</summary>
    private const double WindowSeconds = 1.5;
    /// <summary>Kürzere Fenster gehen nicht ins Clustering und übernehmen den Sprecher des zeitlich nächsten Fensters.</summary>
    private const double MinEmbeddingSeconds = 0.6;
    /// <summary>Höchstzahl Fenster (Distanzmatrix n² · 4 Byte); darüber werden die Fenster länger.</summary>
    private const int MaxWindows = 3000;
    /// <summary>Cosinus-Distanz, bis zu der Cluster bei automatischer Sprecherzahl verschmelzen (kleiner = mehr Sprecher).</summary>
    private const double AutoDistanceThreshold = 0.55;
    /// <summary>Fenster, die mehr als 30 dB (Faktor 0,0316) unter dem Median-Pegel liegen, gelten als Rauschen.</summary>
    private const double QuietRatio = 0.0316;
    /// <summary>Absolute Untergrenze für Sprache (RMS, ≈ −66 dBFS) – darunter ist es sicher kein Gesprochenes.</summary>
    private const double MinSpeechRms = 0.0005;
    /// <summary>Automatisch erkannte „Sprecher“ mit weniger Redezeit sind meist Ausreißer und werden zugeordnet.</summary>
    private const double MinSpeakerSeconds = 2.0;
    /// <summary>Bei vorgegebener Sprecherzahl N zählt ein Cluster erst ab diesem Anteil von (Redezeit / N) als Teilnehmer.</summary>
    private const double MinFairShare = 0.1;

    private readonly object _lock = new();
    private OfflineSpeakerDiarization? _sd;
    private SpeakerEmbeddingExtractor? _extractor;
    private Task? _running;
    private bool _unloadRequested;

    public bool FilesPresent => SegmentationModel.FilesPresent && EmbeddingModel.FilesPresent;

    public bool IsLoaded => _sd != null;

    /// <summary>Lädt (beim ersten Mal: lädt herunter) die Modelle. <paramref name="progress"/> 0…1.</summary>
    public async Task PrepareAsync(Action<double> progress, CancellationToken ct)
    {
        lock (_lock)
        {
            _unloadRequested = false;
            if (_sd != null) { progress(1); return; }
        }

        // Download-Anteil nach Dateigröße gewichten (Segmentierung ≈ 6 MB, Embeddings ≈ 28 MB).
        if (!SegmentationModel.FilesPresent)
            await ModelDownloader.EnsureAsync(SegmentationModel, p => progress(p * 0.15), ct);
        if (!EmbeddingModel.FilesPresent)
            await ModelDownloader.EnsureAsync(EmbeddingModel, p => progress(0.15 + p * 0.75), ct);
        progress(0.9);
        ct.ThrowIfCancellationRequested();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (sd, extractor) = await Task.Run(() =>
        {
            var config = BuildConfig();
            return (new OfflineSpeakerDiarization(config), new SpeakerEmbeddingExtractor(config.Embedding));
        }, ct);
        lock (_lock)
        {
            if (_sd == null) { _sd = sd; _extractor = extractor; }
            else { sd.Dispose(); extractor.Dispose(); }
        }
        Log.Write($"diarizer loaded in {sw.Elapsed.TotalSeconds:0.0}s");
        progress(1);
    }

    /// <summary>Diarisiert eine 16-kHz-Mono-Aufnahme. Liest sie dafür einmal komplett (1 h ≈ 230 MB).</summary>
    /// <param name="expectedSpeakers">Bekannte Teilnehmerzahl, 0 = automatisch. Die Vorgabe zwingt das
    /// Clustering auf genau diese Zahl (hilft bei ähnlichen Stimmen).</param>
    public async Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(string wavPath, int expectedSpeakers,
        Action<double> progress, CancellationToken ct)
    {
        float[]? samples = await Task.Run(() => WavFile.ReadAll(wavPath), ct);
        try
        {
            return await DiarizeAsync(samples, expectedSpeakers, progress, ct);
        }
        finally
        {
            // Das Signal sofort freigeben – danach wird nur noch turnweise aus der Datei gelesen.
            samples = null;
            GC.Collect();
        }
    }

    /// <summary>Diarisiert 16-kHz-Mono-Samples. <paramref name="progress"/> 0…1, von beliebigen Threads.</summary>
    public async Task<IReadOnlyList<SpeakerSegment>> DiarizeAsync(float[] samples, int expectedSpeakers,
        Action<double> progress, CancellationToken ct)
    {
        Task<List<SpeakerSegment>> task;
        lock (_lock)
        {
            var sd = _sd ?? throw new InvalidOperationException("Das Sprechermodell ist nicht geladen.");
            var extractor = _extractor!;
            if (_running != null) throw new InvalidOperationException("Die Sprechererkennung läuft bereits.");
            ct.ThrowIfCancellationRequested();
            task = Task.Run(() => Run(sd, extractor, samples, expectedSpeakers, progress, ct));
            _running = task;
        }
        _ = task.ContinueWith(_ => OnRunFinished(), TaskScheduler.Default);

        // Stufe 1 lässt sich nicht unterbrechen: Bei Abbruch kehren wir sofort zurück, die Modelle
        // werden erst nach Ende des Laufs freigegeben (siehe Unload).
        return await task.WaitAsync(ct);
    }

    /// <summary>Gibt die Modelle frei (RAM). Läuft gerade eine Erkennung, passiert das direkt nach deren Ende.</summary>
    public void Unload()
    {
        OfflineSpeakerDiarization? sd;
        SpeakerEmbeddingExtractor? extractor;
        lock (_lock)
        {
            if (_running != null) { _unloadRequested = true; return; }
            sd = _sd; extractor = _extractor;
            _sd = null; _extractor = null;
        }
        if (sd == null) return;
        sd.Dispose();
        extractor?.Dispose();
        Log.Write("diarizer unloaded");
    }

    public void Dispose() => Unload();

    private void OnRunFinished()
    {
        bool unload;
        lock (_lock)
        {
            _running = null;
            unload = _unloadRequested;
            _unloadRequested = false;
        }
        if (unload) Unload();
    }

    // MARK: Pipeline

    private sealed class Window
    {
        public int Region;
        public double Start, End;
        public float[]? Embedding;
        public int Label = -1;
        public double Duration => End - Start;
    }

    private static List<SpeakerSegment> Run(OfflineSpeakerDiarization sd, SpeakerEmbeddingExtractor extractor,
        float[] samples, int expectedSpeakers, Action<double> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var audioSeconds = samples.Length / (double)WavFile.SampleRate;

        // Stufe 1: Sprachabschnitte + Wechselpunkte (Fortschritt 0–60 %).
        OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
        {
            if (total > 0) progress(0.6 * Math.Clamp((double)done / total, 0, 1));
            return 0;
        };
        var raw = sd.ProcessWithCallback(samples, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        var stageOne = sw.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();

        var regions = Regions(raw, audioSeconds);
        var windows = SplitIntoWindows(regions);
        var quiet = DropQuietWindows(windows, samples);
        if (windows.Count == 0)
        {
            Log.Write($"diarization: no speech in {audioSeconds:0.0}s audio");
            return new List<SpeakerSegment>();
        }

        // Stufe 2: Embeddings je Fenster (60–95 %).
        var embedded = windows.Where(w => w.Duration >= MinEmbeddingSeconds).ToList();
        if (embedded.Count == 0) embedded = new List<Window> { windows.OrderByDescending(w => w.Duration).First() };
        for (var i = 0; i < embedded.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            embedded[i].Embedding = Embed(extractor, samples, embedded[i]);
            if (i % 20 == 0) progress(0.6 + 0.35 * i / embedded.Count);
        }
        embedded.RemoveAll(w => w.Embedding == null);
        if (embedded.Count == 0)
        {
            Log.Write("diarization: no valid embeddings");
            return windows.Select(w => new SpeakerSegment("speaker_0", w.Start, w.End)).ToList();
        }

        var tree = new Dendrogram(embedded.Select(w => w.Embedding!).ToArray());
        var labels = expectedSpeakers > 0
            ? ForcedCut(tree, embedded, expectedSpeakers)
            : tree.CutAtDistance(AutoDistanceThreshold);
        for (var i = 0; i < embedded.Count; i++) embedded[i].Label = labels[i];
        AbsorbTinySpeakers(embedded, expectedSpeakers);
        AssignShortWindows(windows);
        Smooth(windows);
        progress(1);

        var segments = ToSegments(windows);
        Log.Write($"diarization: {audioSeconds:0.0}s audio in {sw.Elapsed.TotalSeconds:0.0}s (segmentation {stageOne:0.0}s), " +
                  $"{raw.Length} raw segments, {windows.Count} windows ({quiet} quiet dropped) → {segments.Count} segments, " +
                  $"{segments.Select(s => s.SpeakerId).Distinct().Count()} speakers" +
                  (expectedSpeakers > 0 ? $" (forced {expectedSpeakers})" : ""));
        return segments;
    }

    /// <summary>Überschneidungsfreie Sprachabschnitte aus den sherpa-Segmenten (Überlappungen werden gekappt).</summary>
    private static List<(double Start, double End)> Regions(OfflineSpeakerDiarizationSegment[] raw, double audioSeconds)
    {
        var regions = new List<(double Start, double End)>();
        double lastEnd = 0;
        foreach (var s in raw.OrderBy(s => s.Start))
        {
            var start = Math.Max(s.Start, lastEnd);
            var end = Math.Min(s.End, audioSeconds);
            if (end - start < 0.05) continue;
            regions.Add((start, end));
            lastEnd = end;
        }
        return regions;
    }

    /// <summary>Teilt jeden Abschnitt in gleich lange Fenster von ≈ <see cref="WindowSeconds"/>.</summary>
    private static List<Window> SplitIntoWindows(List<(double Start, double End)> regions)
    {
        var speech = regions.Sum(r => r.End - r.Start);
        var target = Math.Max(WindowSeconds, speech / MaxWindows);
        var windows = new List<Window>();
        for (var r = 0; r < regions.Count; r++)
        {
            var (start, end) = regions[r];
            var k = Math.Max(1, (int)Math.Round((end - start) / target));
            var len = (end - start) / k;
            for (var i = 0; i < k; i++)
                windows.Add(new Window { Region = r, Start = start + i * len, End = i == k - 1 ? end : start + (i + 1) * len });
        }
        return windows;
    }

    /// <summary>
    /// Entfernt Fenster, die weit leiser sind als die typische Sprache des Meetings (Grundrauschen, Lüfter,
    /// ferne Hintergrundgespräche). Die Segmentierung meldet so etwas gelegentlich als Sprache; es würde dann
    /// als eigener „Sprecher“ geclustert und von der ASR mit erfundenen Wörtern gefüllt. Gibt die Anzahl zurück.
    /// </summary>
    private static int DropQuietWindows(List<Window> windows, float[] samples)
    {
        if (windows.Count == 0) return 0;
        var rms = windows.Select(w => Rms(samples, w)).ToArray();
        var typical = rms.OrderBy(r => r).ElementAt(rms.Length / 2);
        var floor = Math.Max(MinSpeechRms, typical * QuietRatio);
        var keep = new List<Window>(windows.Count);
        for (var i = 0; i < windows.Count; i++)
            if (rms[i] >= floor) keep.Add(windows[i]);
        var dropped = windows.Count - keep.Count;
        windows.Clear();
        windows.AddRange(keep);
        return dropped;
    }

    private static double Rms(float[] samples, Window w)
    {
        var from = Math.Clamp((int)(w.Start * WavFile.SampleRate), 0, samples.Length);
        var to = Math.Clamp((int)(w.End * WavFile.SampleRate), from, samples.Length);
        if (to <= from) return 0;
        double sum = 0;
        for (var i = from; i < to; i++) sum += samples[i] * samples[i];
        return Math.Sqrt(sum / (to - from));
    }

    private static float[]? Embed(SpeakerEmbeddingExtractor extractor, float[] samples, Window w)
    {
        var from = Math.Clamp((int)(w.Start * WavFile.SampleRate), 0, samples.Length);
        var to = Math.Clamp((int)(w.End * WavFile.SampleRate), from, samples.Length);
        if (to - from < WavFile.SampleRate / 4) return null;
        var stream = extractor.CreateStream();
        try
        {
            stream.AcceptWaveform(WavFile.SampleRate, samples[from..to]);
            stream.InputFinished();
            if (!extractor.IsReady(stream)) return null;
            return Normalize(extractor.Compute(stream));
        }
        finally
        {
            (stream as IDisposable)?.Dispose();
        }
    }

    private static float[]? Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v)
        {
            if (float.IsNaN(x)) return null;
            sum += x * x;
        }
        if (sum <= 0) return null;
        var inv = (float)(1 / Math.Sqrt(sum));
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = v[i] * inv;
        return r;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// Vorgegebene Sprecherzahl N: Ausreißer (Husten, Rauschen, ein einzelnes „mhm“) bilden beim Average
    /// Linkage eigene Mini-Cluster und würden sonst einen der N Plätze belegen. Deshalb so lange feiner
    /// schneiden, bis N Cluster mit nennenswerter Redezeit existieren; die Mini-Cluster werden danach zugeordnet.
    /// </summary>
    private static int[] ForcedCut(Dendrogram tree, List<Window> embedded, int expected)
    {
        // Ein Teilnehmer muss wenigstens einen Bruchteil seines „fairen Anteils“ (Redezeit / N) haben.
        var minSeconds = Math.Max(MinSpeakerSeconds, MinFairShare * embedded.Sum(w => w.Duration) / expected);
        var max = Math.Min(tree.Count, expected + 8);
        for (var k = expected; k <= max; k++)
        {
            var labels = tree.Cut(k);
            var big = labels
                .Select((label, i) => (label, embedded[i].Duration))
                .GroupBy(x => x.label)
                .Count(g => g.Sum(x => x.Duration) >= minSeconds);
            if (big >= expected) return labels;
        }
        return tree.Cut(Math.Min(expected, tree.Count));
    }

    /// <summary>
    /// Agglomeratives Clustering (Average Linkage, Cosinus-Distanz) per Nearest-Neighbor-Chain in O(n²).
    /// Labels der Schnitte sind 0…k−1 in Reihenfolge des ersten Auftretens.
    /// </summary>
    internal sealed class Dendrogram
    {
        private readonly List<(int A, int B, float Dist)> _merges;

        public int Count { get; }

        /// <param name="x">L2-normierte Embeddings</param>
        public Dendrogram(float[][] x)
        {
            var n = x.Length;
            Count = n;
            _merges = new List<(int, int, float)>(Math.Max(0, n - 1));
            if (n < 2) return;

            var d = new float[(long)n * n];
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    d[(long)i * n + j] = d[(long)j * n + i] = 1 - Dot(x[i], x[j]);

            var size = Enumerable.Repeat(1, n).ToArray();
            var active = Enumerable.Repeat(true, n).ToArray();
            var chain = new List<int>();
            var activeCount = n;
            while (activeCount > 1)
            {
                if (chain.Count == 0) chain.Add(Array.IndexOf(active, true));
                var a = chain[^1];
                var prev = chain.Count > 1 ? chain[^2] : -1;
                var best = prev;
                var bestD = prev >= 0 ? d[(long)a * n + prev] : float.MaxValue;
                for (var j = 0; j < n; j++)
                {
                    if (!active[j] || j == a) continue;
                    var dj = d[(long)a * n + j];
                    if (dj < bestD) { bestD = dj; best = j; }
                }
                if (best != prev) { chain.Add(best); continue; }

                // a und prev sind gegenseitig nächste Nachbarn → verschmelzen (Index a bleibt, prev entfällt).
                chain.RemoveRange(chain.Count - 2, 2);
                _merges.Add((a, prev, bestD));
                var sa = size[a];
                var sb = size[prev];
                for (var k = 0; k < n; k++)
                {
                    if (!active[k] || k == a || k == prev) continue;
                    var v = (sa * d[(long)a * n + k] + sb * d[(long)prev * n + k]) / (sa + sb);
                    d[(long)a * n + k] = d[(long)k * n + a] = v;
                }
                size[a] = sa + sb;
                active[prev] = false;
                activeCount--;
            }

            // Average Linkage ist reduzibel: nach Höhe sortiert ergeben die Verschmelzungen das Dendrogramm.
            _merges.Sort((p, q) => p.Dist.CompareTo(q.Dist));
        }

        /// <summary>Genau <paramref name="clusters"/> Cluster (1…Count).</summary>
        public int[] Cut(int clusters) => Apply(Math.Clamp(Count - clusters, 0, _merges.Count));

        /// <summary>Verschmelzen, solange die Distanz höchstens <paramref name="threshold"/> beträgt.</summary>
        public int[] CutAtDistance(double threshold) => Apply(_merges.Count(m => m.Dist <= threshold));

        private int[] Apply(int merges)
        {
            var parent = Enumerable.Range(0, Count).ToArray();
            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }
            for (var m = 0; m < merges; m++) parent[Find(_merges[m].A)] = Find(_merges[m].B);

            var map = new Dictionary<int, int>();
            var labels = new int[Count];
            for (var i = 0; i < Count; i++)
            {
                var root = Find(i);
                if (!map.TryGetValue(root, out var label)) { label = map.Count; map[root] = label; }
                labels[i] = label;
            }
            return labels;
        }
    }

    /// <summary>
    /// Den Sprecher mit der wenigsten Redezeit wiederholt dem ähnlichsten anderen zuschlagen: automatisch,
    /// solange er unter <see cref="MinSpeakerSeconds"/> liegt; bei vorgegebener Zahl, bis genau so viele übrig sind.
    /// </summary>
    private static void AbsorbTinySpeakers(List<Window> embedded, int expected)
    {
        while (true)
        {
            var durations = embedded.GroupBy(w => w.Label).ToDictionary(g => g.Key, g => g.Sum(w => w.Duration));
            if (durations.Count < 2) return;
            var (tiny, seconds) = durations.OrderBy(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();
            if (expected > 0 ? durations.Count <= expected : seconds >= MinSpeakerSeconds) return;
            var centroids = Centroids(embedded.Where(w => w.Label != tiny));
            foreach (var w in embedded.Where(w => w.Label == tiny))
                w.Label = centroids.OrderByDescending(c => Dot(c.Value, w.Embedding!)).First().Key;
        }
    }

    private static Dictionary<int, float[]> Centroids(IEnumerable<Window> windows) =>
        windows.GroupBy(w => w.Label).ToDictionary(g => g.Key, g =>
        {
            var dim = g.First().Embedding!.Length;
            var c = new float[dim];
            foreach (var w in g)
                for (var i = 0; i < dim; i++) c[i] += w.Embedding![i];
            return Normalize(c) ?? c;
        });

    /// <summary>Fenster ohne Embedding übernehmen den Sprecher des zeitlich nächsten Fensters (bevorzugt im selben Abschnitt).</summary>
    private static void AssignShortWindows(List<Window> windows)
    {
        var labeled = windows.Where(w => w.Label >= 0).ToList();
        foreach (var w in windows.Where(w => w.Label < 0))
        {
            var mid = (w.Start + w.End) / 2;
            w.Label = labeled
                .OrderBy(o => o.Region == w.Region ? 0 : 1)
                .ThenBy(o => Math.Abs((o.Start + o.End) / 2 - mid))
                .First().Label;
        }
    }

    /// <summary>Einzelne Ausreißer-Fenster mitten in einem durchgehenden Abschnitt glätten (A B A → A A A).</summary>
    private static void Smooth(List<Window> windows)
    {
        for (var i = 1; i + 1 < windows.Count; i++)
        {
            var (p, w, n) = (windows[i - 1], windows[i], windows[i + 1]);
            if (p.Region == w.Region && n.Region == w.Region && p.Label == n.Label && w.Label != p.Label)
                w.Label = p.Label;
        }
    }

    /// <summary>Aufeinanderfolgende Fenster desselben Sprechers im selben Abschnitt zu Segmenten zusammenfassen.</summary>
    private static List<SpeakerSegment> ToSegments(List<Window> windows)
    {
        var segments = new List<SpeakerSegment>();
        Window? open = null;
        double start = 0, end = 0;
        foreach (var w in windows)
        {
            if (open != null && open.Region == w.Region && open.Label == w.Label)
            {
                end = w.End;
                continue;
            }
            if (open != null) segments.Add(new SpeakerSegment($"speaker_{open.Label}", start, end));
            open = w;
            start = w.Start;
            end = w.End;
        }
        if (open != null) segments.Add(new SpeakerSegment($"speaker_{open.Label}", start, end));
        return segments;
    }

    private static OfflineSpeakerDiarizationConfig BuildConfig()
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = SegmentationModel.PathOf("model.onnx");
        config.Segmentation.Pyannote.WindowShiftRatio = WindowShiftRatio;
        config.Segmentation.NumThreads = ModelPaths.NumThreads;
        config.Segmentation.Provider = "cpu";
        config.Segmentation.Debug = 0;
        config.Embedding.Model = EmbeddingModel.PathOf(EmbeddingModel.Files[0]);
        config.Embedding.NumThreads = ModelPaths.NumThreads;
        config.Embedding.Provider = "cpu";
        config.Embedding.Debug = 0;
        // Die Labels der ersten Stufe werden nicht verwendet (siehe Nachclustern).
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = 0.5f;
        config.MinDurationOn = MinDurationOn;
        config.MinDurationOff = MinDurationOff;
        return config;
    }
}
