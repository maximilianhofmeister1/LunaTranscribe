using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaMCP.Embedding;
using LunaMCP.Platform;
using LunaMCP.Server;
using ModelContextProtocol.Server;

namespace LunaMCP;

/// <summary>
/// luna-mcp – der MCP-Server von LunaTranscribe. Claude Code / Claude Desktop starten ihn als Kindprozess und sprechen
/// JSON-RPC über stdin/stdout. Deshalb gilt: <b>Im Serverbetrieb gehört stdout ausschließlich JSON-RPC.</b> Logs gehen
/// nach stderr und in <c>%LOCALAPPDATA%\LunaTranscribe\luna-mcp.log</c>.
/// <code>
///   luna-mcp                               MCP-Server über stdio (so tragen Claude Code und Claude Desktop ihn ein)
///   luna-mcp --check                       Einrichtung prüfen (Ordner, Schalter, Index, Modell) – nur lesend
///   luna-mcp --selftest                    Funktionsprobe mit Beispiel-Meetings in einem temporären Ordner
///   luna-mcp --index                       Suchindex abgleichen und fehlende Vektoren berechnen (für die App)
///   luna-mcp --download-embedding-model    Modell für die inhaltliche Suche laden (für die App, mit Fortschritt)
///   luna-mcp --library &lt;pfad&gt;              anderen Meeting-Ordner verwenden (auch: LUNA_MEETING_FOLDER)
///   luna-mcp --version | --help
/// </code>
/// </summary>
public static class Program
{
    private static readonly EmbeddingModelKind Kind = EmbeddingModelKinds.Standard;
    private static OnnxTextEmbedder? _embedder;
    private static readonly object EmbedderGate = new();

    public static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static async Task<int> Main(string[] args)
    {
        var library = Value(args, "--library") ?? Environment.GetEnvironmentVariable("LUNA_MEETING_FOLDER");
        if (!string.IsNullOrEmpty(library)) library = AppPaths.MeetingFolder(library);

        if (args.Contains("--help") || args.Contains("-h")) return Print(Help());
        if (args.Contains("--version")) return Print("luna-mcp " + Version);
        if (args.Contains("--check")) return PrintAndExit(Diagnostics.Check(MakeEnvironment(library)));
        if (args.Contains("--selftest")) return await SelfTestAsync();
        if (args.Contains("--index")) return await IndexAsync(library);
        if (args.Contains("--download-embedding-model")) return await DownloadAsync();
        if (args.Contains("--confirm-selftest")) return await ConfirmSelfTestAsync();
        return await ServeAsync(library);
    }

    // MARK: Serverbetrieb

    private static async Task<int> ServeAsync(string? library)
    {
        // Sicherheitsnetz: Was versehentlich über Console.Out geschrieben wird, landet auf stderr statt im Protokoll.
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetError(stderr);
        Console.SetOut(stderr);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => McpLog.Write("Absturz: " + e.ExceptionObject);

        McpLog.Write($"luna-mcp {Version} startet (stdio)");
        try
        {
            using var server = new LunaMcpServer(MakeEnvironment(library));
            var transport = new StdioServerTransport(LunaMcpServer.Name, null);
            await server.RunAsync(transport);
            McpLog.Write("luna-mcp beendet");
            return 0;
        }
        catch (Exception ex)
        {
            McpLog.Write("luna-mcp abgebrochen: " + ex);
            return 1;
        }
        finally
        {
            lock (EmbedderGate) _embedder?.Dispose();
        }
    }

    private static ServerEnvironment MakeEnvironment(string? library) => new()
    {
        Settings = () => ReadSettings(),
        LibraryOverride = library,
        Embedder = CurrentEmbedder,
        ModelStatus = ModelStatus,
        Confirmer = new TaskDialogConfirmer(),
        Version = Version,
    };

    /// <summary>Lädt das Embedding-Modell erst bei der ersten inhaltlichen Suche und nur, wenn es lokal liegt.</summary>
    private static ITextEmbedder? CurrentEmbedder()
    {
        if (!Kind.IsDownloaded()) return null;
        lock (EmbedderGate) return _embedder ??= new OnnxTextEmbedder(Kind);
    }

    /// <summary>
    /// Einstellungen der App. <c>LUNA_SETTINGS_FILE</c> lenkt auf eine andere settings.json um – nur für Tests
    /// (zusammen mit <c>LUNA_DATA_DIR</c> und <c>--library</c> läuft luna-mcp dann völlig getrennt von der echten Einrichtung).
    /// </summary>
    private static SharedSettings ReadSettings() =>
        SharedSettings.Read(Environment.GetEnvironmentVariable("LUNA_SETTINGS_FILE") is { Length: > 0 } path ? path : null);

    private static string ModelStatus() =>
        Kind.IsDownloaded() ? $"Modell {Kind.HuggingFaceID()} liegt lokal" : $"Modell {Kind.HuggingFaceID()} nicht geladen";

    // MARK: Befehle

    private static async Task<int> SelfTestAsync()
    {
        UseUtf8Console();
        McpLog.Stderr = false;
        var embedder = ReadSettings().SemanticSearchEnabled ? CurrentEmbedder() : null;
        var (passed, report) = await Diagnostics.SelfTestAsync(embedder, Version);
        Console.WriteLine(report);
        Console.WriteLine(passed ? "Selbsttest bestanden." : "Selbsttest FEHLGESCHLAGEN.");
        lock (EmbedderGate) _embedder?.Dispose();
        return passed ? 0 : 1;
    }

    /// <summary>
    /// Für die App: Stichwort-Index abgleichen und – wenn eingeschaltet und geladen – fehlende Vektoren berechnen.
    /// Ausgabe zeilenweise zum Mitlesen: <c>sync …</c>, <c>progress &lt;Anteil&gt;</c>, <c>stats &lt;Meetings&gt; &lt;Abschnitte&gt; &lt;mit Vektor&gt;</c>.
    /// Darf jederzeit beendet werden (SQLite-Transaktionen): Die App bricht ab, sobald ein Meeting verarbeitet wird.
    /// </summary>
    private static async Task<int> IndexAsync(string? library)
    {
        UseUtf8Console();
        McpLog.Stderr = false;
        var settings = ReadSettings();
        if (!settings.McpAccessEnabled)
        {
            Console.WriteLine("Meetings sind nicht für Claude freigegeben – kein Suchindex nötig.");
            return 0;
        }
        try
        {
            var environment = MakeEnvironment(library);
            var snapshot = new MeetingCatalog().Snapshot(environment.LibraryFolder(settings), environment.UnsavedFolder);
            using var index = new SearchIndex(environment.IndexPath);
            var report = index.Sync(snapshot.Meetings);
            Console.WriteLine($"sync +{report.Added} ~{report.Updated} -{report.Removed} ={report.Unchanged}");
            if (report.HasChanges) McpLog.Write($"Index abgeglichen: +{report.Added} ~{report.Updated} -{report.Removed}");

            string? model = null;
            if (settings.SemanticSearchEnabled && CurrentEmbedder() is { } embedder)
            {
                model = embedder.ModelID;
                var before = index.Stats(model);
                var pending = Math.Max(1, before.Chunks - before.Embedded);
                var done = await index.EmbedPendingAsync(embedder, OnnxTextEmbedder.BatchSize, progress: (count, _) =>
                    Console.WriteLine(FormattableString.Invariant($"progress {Math.Min(1, (double)count / pending):0.000}")));
                if (done > 0) McpLog.Write($"{done} Abschnitte eingebettet");
            }
            var stats = index.Stats(model);
            Console.WriteLine($"stats {stats.Meetings} {stats.Chunks} {stats.Embedded}");
            return 0;
        }
        catch (Exception ex)
        {
            McpLog.Write("Indexieren fehlgeschlagen: " + ex);
            Console.WriteLine("error " + ex.Message);
            return 1;
        }
        finally
        {
            lock (EmbedderGate) _embedder?.Dispose();
        }
    }

    /// <summary>Für die App: Modell laden; Fortschritt als <c>progress &lt;Anteil&gt;</c>, Ende als <c>done</c> oder <c>error …</c>.</summary>
    private static async Task<int> DownloadAsync()
    {
        UseUtf8Console();
        McpLog.Stderr = false;
        try
        {
            var last = -1.0;
            await EmbeddingModelDownloader.DownloadAsync(Kind, fraction =>
            {
                if (fraction - last < 0.005 && fraction < 1) return;
                last = fraction;
                Console.WriteLine(FormattableString.Invariant($"progress {fraction:0.000}"));
            }, CancellationToken.None);

            // Plausibilität: ein falsch geladenes Modell sucht sonst nur still schlechter.
            using var embedder = new OnnxTextEmbedder(Kind);
            var (ok, detail) = await Diagnostics.EmbeddingSanityAsync(embedder);
            McpLog.Write("Embedding-Modell geladen: " + detail);
            if (!ok)
            {
                Console.WriteLine("error Das Modell liefert unplausible Ergebnisse: " + detail);
                return 1;
            }
            Console.WriteLine("done " + detail);
            return 0;
        }
        catch (Exception ex)
        {
            McpLog.Write("Modell-Download fehlgeschlagen: " + ex);
            Console.WriteLine("error " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Entwickler-Probe des echten Bestätigungsdialogs: zeigt ihn mit 5 s Zeitlimit. Ohne Klick muss „keine Antwort“
    /// herauskommen; Enter muss „abgelehnt“ ergeben (Standardtaste „Abbrechen“). Löscht nichts.
    /// </summary>
    private static async Task<int> ConfirmSelfTestAsync()
    {
        UseUtf8Console();
        var meeting = SampleLibrary.Meetings()[0];
        var request = ConfirmationRequest.Delete(new[] { MeetingDescription.Of(meeting) });
        var outcome = await new TaskDialogConfirmer { Timeout = TimeSpan.FromSeconds(5) }.ConfirmAsync(request, "luna-mcp-selftest");
        Console.WriteLine($"Ergebnis: {outcome.Kind}{(outcome.Reason is null ? "" : " – " + outcome.Reason)}");
        return outcome.Kind is ConfirmationOutcomeKind.TimedOut or ConfirmationOutcomeKind.Declined ? 0 : 1;
    }

    // MARK: Hilfen

    private static string Help()
    {
        var path = Environment.ProcessPath ?? "luna-mcp.exe";
        return string.Join("\n",
            $"luna-mcp {Version} – MCP-Server für die Meetings aus LunaTranscribe",
            "",
            "  luna-mcp                               MCP-Server über stdio (für Claude Code / Claude Desktop)",
            "  luna-mcp --check                       Einrichtung prüfen (nur lesend)",
            "  luna-mcp --selftest                    Funktionsprobe mit Beispiel-Meetings",
            "  luna-mcp --index                       Suchindex abgleichen (nutzt die App)",
            "  luna-mcp --download-embedding-model    Modell für die inhaltliche Suche laden (nutzt die App)",
            "  luna-mcp --library <pfad>              anderen Meeting-Ordner verwenden",
            "  luna-mcp --version",
            "",
            $"Claude Code:    claude mcp add --scope user --transport stdio lunatranscribe -- \"{path}\"");
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void UseUtf8Console()
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
    }

    private static int Print(string text)
    {
        UseUtf8Console();
        Console.WriteLine(text);
        return 0;
    }

    private static int PrintAndExit(string text)
    {
        McpLog.Stderr = false;
        return Print(text);
    }
}
