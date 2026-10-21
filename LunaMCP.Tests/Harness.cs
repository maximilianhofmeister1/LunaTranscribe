using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaMCP.Server;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LunaMCP.Tests;

/// <summary>
/// Startet einen echten <see cref="LunaMcpServer"/> gegen eine temporäre Bibliothek und verbindet einen echten
/// MCP-Client über Pipes – also derselbe Protokollweg wie über stdio.
/// </summary>
internal sealed class Harness : IAsyncDisposable
{
    public string Root { get; }
    public string Library { get; }
    public ServerEnvironment Environment { get; }
    public ScriptedConfirmer Confirmer { get; } = new();
    public FakeRecycleBin RecycleBin { get; }
    private SharedSettings _settings;
    private readonly List<InProcessConnection> _connections = new();
    private readonly List<LunaMcpServer> _servers = new();

    /// <summary>Einstellungen „der App“; Änderungen wirken bei der nächsten Anfrage.</summary>
    public SharedSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value);
    }

    public Harness(SharedSettings? settings = null, ITextEmbedder? embedder = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "LunaMCPTests-" + Guid.NewGuid().ToString("N")[..8]);
        Library = Path.Combine(Root, "Library");
        Directory.CreateDirectory(Library);
        _settings = settings ?? new SharedSettings { McpAccessEnabled = true };
        RecycleBin = new FakeRecycleBin(Path.Combine(Root, "Papierkorb"));
        Environment = new ServerEnvironment
        {
            Settings = () => Settings,
            LibraryOverride = Library,
            UnsavedFolder = Path.Combine(Root, "Unsaved"),
            QueueFolder = Path.Combine(Root, "Queue"),
            IndexPath = Path.Combine(Root, "Index", "search.sqlite"),
            ActivityLog = new ActivityLog(Path.Combine(Root, "activity.log")),
            Embedder = () => embedder,
            Confirmer = Confirmer,
            RecycleBin = RecycleBin,
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"),
            Version = "test",
            ChangePollInterval = null,
        };
    }

    public string Add(MeetingTranscript meeting) => MeetingLibrary.Write(meeting, Library, null);

    public LunaMcpServer Server()
    {
        var server = new LunaMcpServer(Environment);
        _servers.Add(server);
        return server;
    }

    public async Task<McpClient> ConnectAsync()
    {
        var connection = await InProcessConnection.StartAsync(Server(), "test-client", "1.0", clientTitle: "Testclient");
        _connections.Add(connection);
        return connection.Client;
    }

    public List<ActivityEntry> Activity => Environment.ActivityLog.Entries();

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections) await connection.DisposeAsync();
        foreach (var server in _servers) server.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
    }
}

/// <summary>Bestätigungsdialog-Ersatz: liefert vorgegebene Antworten und merkt sich die Anfragen.</summary>
internal sealed class ScriptedConfirmer : IUserConfirmer
{
    private readonly object _gate = new();
    public List<ConfirmationRequest> Requests { get; } = new();
    public List<string> Clients { get; } = new();
    public ConfirmationOutcome Answer { get; set; } = ConfirmationOutcome.Declined;

    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, string client)
    {
        lock (_gate)
        {
            Requests.Add(request);
            Clients.Add(client);
            return Task.FromResult(Answer);
        }
    }
}

/// <summary>Papierkorb-Attrappe: verschiebt in einen eigenen Ordner statt in den echten Papierkorb.</summary>
internal sealed class FakeRecycleBin : IRecycleBin
{
    private readonly string _folder;
    public List<string> Trashed { get; } = new();

    public FakeRecycleBin(string folder) => _folder = folder;

    public void MoveToRecycleBin(string folder)
    {
        Directory.CreateDirectory(_folder);
        Trashed.Add(folder);
        Directory.Move(folder, MeetingLibrary.UniquePath(_folder, Path.GetFileName(folder)));
    }
}

/// <summary>Ersatz-Embedding (Bag-of-Words mit Synonymen), damit die inhaltliche Suche ohne Modell prüfbar ist.</summary>
internal sealed class SynonymEmbedder : ITextEmbedder
{
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["teurer"] = "preis", ["preise"] = "preis", ["preiserhohung"] = "preis", ["kosten"] = "preis",
    };

    public string ModelID => "test-synonyms";

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(Vector).ToArray());

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(Vector(text));

    private static float[] Vector(string text)
    {
        var v = new float[32];
        foreach (var word in Words(TextFolding.Fold(text)))
        {
            var key = Synonyms.TryGetValue(word, out var s) ? s : word;
            uint hash = 2166136261;
            foreach (var c in key) hash = unchecked((hash ^ c) * 16777619);
            v[hash % 32] += 1;
        }
        return v;
    }

    private static IEnumerable<string> Words(string text) =>
        new string(text.Select(c => char.IsLetter(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Liefert für jeden Text denselben Vektor – ein kaputtes Modell.</summary>
internal sealed class ConstantEmbedder : ITextEmbedder
{
    public string ModelID => "constant";
    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(_ => new float[] { 1, 0, 0 }).ToArray());
    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new float[] { 1, 0, 0 });
}

internal static class Samples
{
    public static readonly DateTime Date = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    public static MeetingTranscript Meeting(string title, (string Speaker, string Text)[] lines, double daysAgo = 0, string? summary = null,
                                            Dictionary<string, string>? names = null, bool confidential = false)
    {
        var turns = lines.Select((line, index) => new MeetingTranscript.Turn(line.Speaker, index * 30, index * 30 + 25, line.Text)).ToList();
        return new MeetingTranscript(Date.AddDays(-daysAgo), lines.Length * 30, EngineKind.Parakeet, AppLanguage.German, turns, title, names)
        {
            Summary = summary is null ? null : new MeetingSummary(summary, Date),
            IsConfidential = confidential ? true : null,
        };
    }

    public static readonly MeetingTranscript Planning = Meeting("Planung Q4", new[]
    {
        ("Sprecher 1", "Dann lass uns mit dem Budget anfangen."),
        ("Sprecher 2", "Wir müssen die Preise im nächsten Quartal erhöhen, sonst reicht das Budget nicht."),
        ("Sprecher 1", "Einverstanden, Tom kümmert sich bis Freitag um das Angebot."),
    }, summary: "## Kurzfassung\nPreiserhöhung beschlossen.\n\n## Aufgaben\n- [ ] Tom: Angebot bis Freitag",
       names: new Dictionary<string, string> { ["Sprecher 1"] = "Anna" });

    public static readonly MeetingTranscript Retro = Meeting("Retro Sprint 12", new[]
    {
        ("Sprecher 1", "Das Deployment am Freitag war holprig."),
        ("Sprecher 2", "Ehrlich gesagt sollten wir teurer werden."),
    }, daysAgo: 3);

    public static readonly MeetingTranscript Secret = Meeting("Personalgespräch", new[] { ("Sprecher 1", "Gehalt und Budget.") },
        daysAgo: 1, confidential: true);
}

internal sealed record ToolResponse(string Text, bool IsError, JsonElement? Structured);

internal static class ClientExtensions
{
    /// <summary>Tool aufrufen und Text + Fehlerflag + strukturierte Ausgabe zurückgeben.</summary>
    public static async Task<ToolResponse> CallAsync(this McpClient client, string name, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(name, arguments ?? new Dictionary<string, object?>());
        return new ToolResponse(Results.TextOf(result.Content), result.IsError == true, result.StructuredContent);
    }
}
