using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LunaTranscribe.Engine;

/// <summary>Beschreibt ein Modell auf Hugging Face: Repo + benötigte Dateien.</summary>
public sealed record ModelSpec(string FolderName, string HuggingFaceRepo, string[] Files)
{
    public string Directory => Path.Combine(ModelPaths.Base, FolderName);
    public string PathOf(string file) => Path.Combine(Directory, file);
    public bool FilesPresent => Files.All(f => File.Exists(PathOf(f)));
    public string UrlOf(string file) => $"https://huggingface.co/{HuggingFaceRepo}/resolve/main/{file}";
}

/// <summary>
/// Einzige Netzwerkverbindung der App: einmaliger Modell-Download von Hugging Face.
/// Lädt in .part-Dateien, setzt abgebrochene Downloads fort (HTTP Range) und benennt erst nach
/// vollständigem Download um – eine vorhandene Zieldatei gilt daher als vollständig.
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LunaTranscribe/" + typeof(ModelDownloader).Assembly.GetName().Version?.ToString(3) + " (Windows)");
        return c;
    }

    public static async Task EnsureAsync(ModelSpec spec, Action<double> progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(spec.Directory);
        var missing = spec.Files.Where(f => !File.Exists(spec.PathOf(f))).ToList();
        if (missing.Count == 0) { progress(1); return; }

        // Gesamtgröße ermitteln, damit der Fortschritt über alle Dateien hinweg stimmt.
        var sizes = new Dictionary<string, long>();
        foreach (var f in missing)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, spec.UrlOf(f));
            using var resp = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new EngineException($"Modell-Datei nicht gefunden: {f} (HTTP {(int)resp.StatusCode})");
            sizes[f] = resp.Content.Headers.ContentLength ?? 0;
        }
        var total = Math.Max(1, sizes.Values.Sum());
        long doneBefore = 0;

        foreach (var f in missing)
        {
            var target = spec.PathOf(f);
            var part = target + ".part";
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var already = doneBefore;
            await DownloadAsync(spec.UrlOf(f), part, sizes[f], bytes => progress(Math.Min(1, (double)(already + bytes) / total)), ct);
            File.Move(part, target, overwrite: true);
            doneBefore += sizes[f];
        }
        progress(1);
    }

    private static async Task DownloadAsync(string url, string partPath, long expectedSize, Action<long> onBytes, CancellationToken ct)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize > 0 && existing > expectedSize) { File.Delete(partPath); existing = 0; }
        if (expectedSize > 0 && existing == expectedSize) { onBytes(existing); return; }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partPath);
            existing = 0;
            using var retry = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp2 = await Http.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
            resp2.EnsureSuccessStatusCode();
            await CopyAsync(resp2, partPath, append: false, 0, onBytes, ct);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var append = resp.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        await CopyAsync(resp, partPath, append, append ? existing : 0, onBytes, ct);
    }

    private static async Task CopyAsync(HttpResponseMessage resp, string partPath, bool append, long offset, Action<long> onBytes, CancellationToken ct)
    {
        await using var net = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 18];
        long done = offset;
        var lastReport = DateTime.UtcNow;
        int n;
        while ((n = await net.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = now;
                onBytes(done);
            }
        }
        onBytes(done);
    }
}
