using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaMCP.Embedding;

/// <summary>
/// Einmaliger Download des Embedding-Modells von Hugging Face (fester Commit). Lädt in .part-Dateien, setzt abgebrochene
/// Downloads fort (HTTP Range), prüft Größe und SHA-256 und schreibt erst danach die Markierung „vollständig“.
/// Läuft nur auf ausdrücklichen Wunsch (<c>luna-mcp --download-embedding-model</c>, gestartet aus den Einstellungen der App),
/// nie im Serverbetrieb.
/// </summary>
public static class EmbeddingModelDownloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LunaTranscribe-luna-mcp/0.1 (Windows)");
        return client;
    }

    /// <param name="progress">Anteil 0…1 über alle Dateien.</param>
    public static async Task DownloadAsync(EmbeddingModelKind kind, Action<double> progress, CancellationToken cancellationToken)
    {
        var folder = kind.LocalFolder();
        Directory.CreateDirectory(folder);
        var marker = kind.LocalPath(EmbeddingModelKinds.CompleteMarker);
        if (kind.IsDownloaded())
        {
            progress(1);
            return;
        }
        if (File.Exists(marker)) File.Delete(marker);

        var files = kind.Files();
        var total = Math.Max(1, files.Sum(f => f.Size));
        long before = 0;
        foreach (var file in files)
        {
            var target = kind.LocalPath(file.LocalName);
            var already = before;
            if (!File.Exists(target) || !await VerifyAsync(target, file, cancellationToken))
            {
                var part = target + ".part";
                await DownloadFileAsync(kind.DownloadUrl(file), part, file.Size,
                    bytes => progress(Math.Min(1, (double)(already + bytes) / total)), cancellationToken);
                if (!await VerifyAsync(part, file, cancellationToken))
                {
                    File.Delete(part);
                    throw new InvalidDataException($"Die Datei {file.LocalName} ist beschädigt (Prüfsumme stimmt nicht). Bitte erneut laden.");
                }
                File.Move(part, target, overwrite: true);
            }
            before += file.Size;
            progress(Math.Min(1, (double)before / total));
        }
        await File.WriteAllTextAsync(marker, $"{kind.HuggingFaceID()}@{kind.Revision()}\n", cancellationToken);
        progress(1);
    }

    private static async Task<bool> VerifyAsync(string path, EmbeddingModelFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (file.Sha256 is null) return info.Length > 0;
        if (info.Length != file.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return hash == file.Sha256;
    }

    private static async Task DownloadFileAsync(string url, string partPath, long expectedSize, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(partPath);
            existing = 0;
        }
        if (expectedSize > 0 && existing == expectedSize)
        {
            onBytes(existing);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partPath);
            await DownloadFileAsync(url, partPath, expectedSize, onBytes, cancellationToken);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download fehlgeschlagen: {url} (HTTP {(int)response.StatusCode})");

        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 18];
        long done = append ? existing : 0;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await network.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
            {
                lastReport = DateTime.UtcNow;
                onBytes(done);
            }
        }
        onBytes(done);
    }
}
