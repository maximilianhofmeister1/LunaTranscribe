using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LunaMCP.Server;

/// <summary>
/// Prüfungen für <c>luna-mcp --selftest</c> / <c>--check</c> und „Verbindung testen“ in der App. Liefern Text statt
/// zu drucken, damit der Aufrufer entscheidet, wohin (im MCP-Betrieb gehört stdout nur JSON-RPC).
/// </summary>
public static class Diagnostics
{
    /// <summary>Prüft die echte Einrichtung, nur lesend: Ordner, Meetings, Schalter, Suchindex, Modell.</summary>
    public static string Check(ServerEnvironment environment)
    {
        var settings = environment.Settings();
        var folder = environment.LibraryFolder(settings);
        var reachable = Directory.Exists(folder);
        var all = MeetingLibrary.Scan(folder).Concat(MeetingLibrary.Scan(environment.UnsavedFolder, unsaved: true)).ToList();
        var confidential = all.Count(m => m.Transcript.Confidential);
        using var server = new LunaMcpServer(environment);
        var context = server.MakeContext();

        static string OnOff(bool value) => value ? "an" : "aus";
        var lines = new List<string>
        {
            $"luna-mcp {environment.Version}",
            $"Meeting-Ordner: {folder} ({(reachable ? "erreichbar" : "NICHT erreichbar")})",
            $"Meetings: {all.Count}" + (confidential > 0 ? $" (davon {confidential} vertraulich – für Claude unsichtbar)" : ""),
            $"Freigabe für Claude: {OnOff(settings.McpAccessEnabled)} · Bearbeiten: {OnOff(settings.McpWriteEnabled)} · Umbenennungen bestätigen: {OnOff(settings.McpConfirmRenames)}",
            $"Inhaltliche Suche: {OnOff(settings.SemanticSearchEnabled)} · {environment.ModelStatus()}",
            server.Search.Stats(context.Snapshot, settings),
        };
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Funktionsprobe gegen eine temporäre Beispiel-Bibliothek: Handshake über einen echten MCP-Client, jedes Tool,
    /// Ressourcen, Prompts. Löschen wird nur mit „Ablehnen“ geprüft – eine echte Zustimmung legte Ordner in den
    /// Papierkorb des Nutzers.
    /// </summary>
    public static async Task<(bool Passed, string Report)> SelfTestAsync(ITextEmbedder? embedder, string version)
    {
        var report = new List<string>();
        var passed = true;
        void Check(string name, bool ok, string detail = "")
        {
            passed = passed && ok;
            report.Add($"{(ok ? "✓" : "✗")} {name}{(detail.Length == 0 ? "" : " – " + detail)}");
        }

        var root = Path.Combine(Path.GetTempPath(), "luna-mcp-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var library = Path.Combine(root, "Library");
        try
        {
            try
            {
                foreach (var meeting in SampleLibrary.Meetings()) MeetingLibrary.Write(meeting, library, null);
            }
            catch (Exception ex)
            {
                return (false, "✗ Beispiel-Bibliothek anlegen – " + ex.Message);
            }

            var settings = new SharedSettings { McpAccessEnabled = true, McpWriteEnabled = true, SemanticSearchEnabled = embedder is not null };
            var environment = new ServerEnvironment
            {
                Settings = () => settings,
                LibraryOverride = library,
                UnsavedFolder = Path.Combine(root, "Unsaved"),
                QueueFolder = Path.Combine(root, "Queue"),
                IndexPath = Path.Combine(root, "search.sqlite"),
                ActivityLog = new ActivityLog(Path.Combine(root, "activity.log")),
                Embedder = () => embedder,
                Confirmer = new FixedConfirmer(ConfirmationOutcome.Declined),
                RecycleBin = new RefusingRecycleBin(),
                Version = version,
                ChangePollInterval = null,
            };

            using var server = new LunaMcpServer(environment);
            await using var connection = await InProcessConnection.StartAsync(server, "luna-mcp-selftest", version);
            var client = connection.Client;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                Check("Handshake", client.ServerInfo?.Name == LunaMcpServer.Name, $"Protokoll {client.NegotiatedProtocolVersion}");
                var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
                Check("Tools", tools.Count == 7, string.Join(", ", tools));

                var list = await client.CallToolAsync("list_meetings", new Dictionary<string, object?>());
                var listText = Results.TextOf(list.Content);
                Check("list_meetings", listText.Contains("3 Meeting(s)") && !listText.Contains(SampleLibrary.SecretTitle), "vertrauliches Meeting unsichtbar");

                var keyword = await client.CallToolAsync("search_meetings", new Dictionary<string, object?> { ["query"] = "Angebot", ["mode"] = "keyword" });
                Check("Stichwortsuche", Results.TextOf(keyword.Content).Contains(SampleLibrary.PlanningTitle));

                if (embedder is not null)
                {
                    await client.CallToolAsync("search_meetings", new Dictionary<string, object?> { ["query"] = "Kosten" });
                    await server.Search.WaitForEmbeddingAsync();
                    var semantic = await client.CallToolAsync("search_meetings", new Dictionary<string, object?>
                    {
                        ["query"] = "Wir müssen mehr Geld verlangen", ["mode"] = "semantic", ["limit"] = 1,
                    });
                    Check("Inhaltliche Suche", Results.TextOf(semantic.Content).Contains(SampleLibrary.PlanningTitle),
                          "„mehr Geld verlangen“ findet die Preiserhöhung");
                }
                else
                {
                    report.Add("– Inhaltliche Suche übersprungen (Modell nicht geladen oder ausgeschaltet)");
                }

                var id = MeetingLibrary.ShortID(SampleLibrary.Meetings()[0].Id);
                var meeting = await client.CallToolAsync("get_meeting", new Dictionary<string, object?> { ["id"] = id });
                var meetingText = Results.TextOf(meeting.Content);
                var revision = Regex.Match(meetingText, "Revision ([0-9a-f]+)").Groups[1].Value;
                Check("get_meeting", revision.Length > 0 && meetingText.Contains("[00:30]"));

                var renamed = await client.CallToolAsync("rename_speaker", new Dictionary<string, object?>
                {
                    ["id"] = id, ["speaker"] = "2", ["name"] = "Tom", ["revision"] = revision,
                });
                Check("rename_speaker", renamed.IsError != true);
                var stale = await client.CallToolAsync("rename_meeting", new Dictionary<string, object?>
                {
                    ["id"] = id, ["title"] = "X", ["revision"] = revision,
                });
                Check("Revisionsschutz", stale.IsError == true, "veraltete Revision abgelehnt");

                var current = MeetingLibrary.Scan(library).FirstOrDefault(m => m.Id == SampleLibrary.Meetings()[0].Id)?.Revision ?? "";
                var delete = await client.CallToolAsync("delete_meetings", new Dictionary<string, object?>
                {
                    ["items"] = new[] { new Dictionary<string, object?> { ["id"] = id, ["revision"] = current } },
                });
                Check("delete_meetings", Results.TextOf(delete.Content).Contains("abgelehnt") && MeetingLibrary.Scan(library).Count == SampleLibrary.Meetings().Count,
                      "abgelehnt → nichts gelöscht");

                var resource = await client.ReadResourceAsync("luna://meetings/" + id);
                Check("Ressourcen", resource.Contents.OfType<TextResourceContents>().FirstOrDefault()?.Text.Contains("# ") == true);
                var prompt = await client.GetPromptAsync("meeting_summary", new Dictionary<string, object?> { ["id"] = id });
                Check("Prompts", prompt.Messages.Count == 2);
                report.Add($"Dauer: {stopwatch.Elapsed.TotalSeconds:0.00} s");
            }
            catch (Exception ex)
            {
                Check("Protokoll", false, ex.Message);
            }

            if (embedder is not null)
            {
                var (ok, detail) = await EmbeddingSanityAsync(embedder);
                Check("Embedding-Modell", ok, detail);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
        return (passed, string.Join("\n", report));
    }

    /// <summary>
    /// Plausibilität der Vektoren: Ein Satz muss seiner Umschreibung näher sein als einem fremden Satz.
    /// Fängt ein falsch geladenes Modell ab (falsche Token, falsches Pooling), das sonst nur still schlechter sucht.
    /// </summary>
    public static async Task<(bool Ok, string Detail)> EmbeddingSanityAsync(ITextEmbedder embedder)
    {
        try
        {
            var query = await embedder.EmbedQueryAsync("Wir müssen die Preise erhöhen.");
            var documents = await embedder.EmbedDocumentsAsync(new[]
            {
                "Die Kosten steigen, wir sollten teurer werden.",
                "Morgen soll es in Hamburg regnen.",
            });
            var related = VectorMath.Cosine(query, documents[0]);
            var unrelated = VectorMath.Cosine(query, documents[1]);
            var detail = FormattableString.Invariant($"{embedder.ModelID}, {query.Length} Dimensionen, ähnlich {related:0.000} vs. fremd {unrelated:0.000}");
            return (related > unrelated + 0.02f, detail);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Papierkorb, der nie etwas verschiebt – der Selbsttest darf nichts löschen.</summary>
    private sealed class RefusingRecycleBin : IRecycleBin
    {
        public void MoveToRecycleBin(string folder) => throw new InvalidOperationException("Der Selbsttest löscht nichts.");
    }
}

/// <summary>
/// Server und echter MCP-Client im selben Prozess, verbunden über zwei anonyme Pipes – derselbe Protokollweg
/// (JSON-RPC-Zeilen über Streams) wie über stdio. Für den Selbsttest und die Tests.
/// </summary>
public sealed class InProcessConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancel;
    private readonly Task _serverTask;
    private readonly Stream[] _pipes;

    public McpClient Client { get; }

    private InProcessConnection(McpClient client, Task serverTask, CancellationTokenSource cancel, Stream[] pipes)
    {
        Client = client;
        _serverTask = serverTask;
        _cancel = cancel;
        _pipes = pipes;
    }

    public static async Task<InProcessConnection> StartAsync(LunaMcpServer server, string clientName, string version, string? clientTitle = null)
    {
        var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);

        var cancel = new CancellationTokenSource();
        var transport = new StreamServerTransport(serverIn, toClient, LunaMcpServer.Name, null);
        var serverTask = Task.Run(() => server.RunAsync(transport, cancel.Token));
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer, clientIn, null),
            new McpClientOptions { ClientInfo = new Implementation { Name = clientName, Title = clientTitle, Version = version } });
        return new InProcessConnection(client, serverTask, cancel, new Stream[] { toServer, serverIn, toClient, clientIn });
    }

    public async ValueTask DisposeAsync()
    {
        try { await Client.DisposeAsync(); } catch (Exception) { }
        _cancel.Cancel();
        foreach (var pipe in _pipes)
        {
            try { pipe.Dispose(); } catch (Exception) { }
        }
        try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _cancel.Dispose();
    }
}

/// <summary>Beispiel-Meetings für den Selbsttest (keine echten Daten).</summary>
public static class SampleLibrary
{
    public const string PlanningTitle = "Selbsttest: Planung Q4";
    public const string SecretTitle = "Selbsttest: Vertraulich";

    private static readonly Lazy<List<MeetingTranscript>> Samples = new(Create);

    public static List<MeetingTranscript> Meetings() => Samples.Value;

    private static List<MeetingTranscript> Create()
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime;
        MeetingTranscript Make(string title, double offset, (string, string)[] lines, bool confidential = false)
        {
            var turns = lines.Select((line, index) => new MeetingTranscript.Turn(line.Item1, index * 30, index * 30 + 25, line.Item2)).ToList();
            return new MeetingTranscript(date.AddSeconds(offset), lines.Length * 30, EngineKind.Parakeet, AppLanguage.German, turns, title)
            {
                IsConfidential = confidential ? true : null,
            };
        }
        return new List<MeetingTranscript>
        {
            Make(PlanningTitle, 0, new[]
            {
                ("Sprecher 1", "Dann lass uns mit dem Budget für das vierte Quartal anfangen."),
                ("Sprecher 2", "Die Preise müssen im nächsten Quartal um zehn Prozent erhöht werden."),
                ("Sprecher 1", "Einverstanden. Tom holt bis Freitag ein Angebot vom Lieferanten ein."),
            }),
            Make("Selbsttest: Retro", -86_400, new[]
            {
                ("Sprecher 1", "Das Deployment am Freitag war holprig, die Tests liefen zu spät."),
                ("Sprecher 2", "Nächstes Mal frieren wir den Code am Mittwoch ein."),
            }),
            Make("Selbsttest: Wetter", -172_800, new[] { ("Sprecher 1", "Morgen soll es regnen, der Ausflug fällt aus.") }),
            Make(SecretTitle, -3_600, new[] { ("Sprecher 1", "Gehaltsgespräch, streng vertraulich.") }, confidential: true),
        };
    }
}
