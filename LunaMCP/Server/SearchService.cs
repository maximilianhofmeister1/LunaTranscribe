using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaMCP.Server;

/// <summary>
/// Hybride Suche für den Server: hält den Suchindex auf dem Stand des Katalogs, berechnet fehlende
/// Vektoren im Hintergrund und führt Stichwort-, Vektor- und Titel-Treffer zusammen.
/// </summary>
internal sealed class SearchService : IDisposable
{
    public enum Mode { Auto, Keyword, Semantic }

    public static string ModeName(Mode mode) => mode.ToString().ToLowerInvariant();

    public static readonly string[] ModeNames = { "auto", "keyword", "semantic" };

    public static Mode? ParseMode(string text) => text switch
    {
        "auto" => Mode.Auto,
        "keyword" => Mode.Keyword,
        "semantic" => Mode.Semantic,
        _ => null,
    };

    public enum SemanticKind { Used, Disabled, ModelMissing, NotRequested, Failed }

    public sealed record SemanticState(SemanticKind Kind, double Coverage = 1, string? Error = null)
    {
        public static readonly SemanticState Disabled = new(SemanticKind.Disabled);
        public static readonly SemanticState ModelMissing = new(SemanticKind.ModelMissing);
        public static readonly SemanticState NotRequested = new(SemanticKind.NotRequested);

        public string? Note => Kind switch
        {
            SemanticKind.Used when Coverage < 0.999 =>
                $"Die inhaltliche Suche indexiert noch ({(int)(Coverage * 100)} % fertig) – ältere Meetings fehlen dort eventuell.",
            SemanticKind.Disabled =>
                "Inhaltliche Suche ist aus (LunaTranscribe → Einstellungen → Datenschutz → „Inhaltliche Suche“). Es wurde nur nach Stichworten gesucht.",
            SemanticKind.ModelMissing =>
                "Das Modell für die inhaltliche Suche ist noch nicht geladen (LunaTranscribe → Einstellungen → Datenschutz → „Inhaltliche Suche“). Es wurde nur nach Stichworten gesucht.",
            SemanticKind.Failed =>
                $"Inhaltliche Suche fehlgeschlagen ({Error}). Es wurde nur nach Stichworten gesucht.",
            _ => null,
        };
    }

    /// <param name="Speaker">Gefalteter Sprechername oder -ID.</param>
    public sealed record Filter(DateTime? From = null, DateTime? To = null, string? Speaker = null);

    public sealed record Result(List<SearchHit> Hits, SemanticState Semantic);

    private readonly string _indexPath;
    private readonly Func<ITextEmbedder?> _embedderProvider;
    private readonly object _gate = new();
    private SearchIndex? _index;
    private int _syncedGeneration = -1;
    private Task? _embedTask;

    public SearchService(string indexPath, Func<ITextEmbedder?> embedder)
    {
        _indexPath = indexPath;
        _embedderProvider = embedder;
    }

    public void Dispose()
    {
        lock (_gate) _index?.Dispose();
    }

    public async Task<Result> SearchAsync(string text, Mode mode, Filter filter, int limit, CatalogSnapshot snapshot, SharedSettings settings)
    {
        var index = Synced(snapshot);
        var meetings = new Dictionary<Guid, StoredMeeting>();
        foreach (var m in snapshot.Meetings) meetings.TryAdd(m.Id, m);
        var query = new KeywordQuery(text, settings.VocabularyEntries);
        var candidates = Math.Max(limit * 6, 50);

        bool Allowed(ChunkRecord record)
        {
            if (!meetings.TryGetValue(record.MeetingID, out var meeting) || !Matches(meeting, filter)) return false;
            if (filter.Speaker is not { } speaker) return true;
            var ids = SpeakerIDs(speaker, meeting.Transcript);
            return ids.Overlaps(record.SpeakerIDs);
        }

        var keyword = mode == Mode.Semantic
            ? new List<KeywordHit>()
            : index.KeywordSearch(query, candidates).Where(h => Allowed(h.Record)).ToList();

        var semantic = new List<VectorHit>();
        var state = SemanticState.NotRequested;
        if (mode != Mode.Keyword)
        {
            (semantic, state) = await SemanticHitsAsync(text, index, settings, candidates).ConfigureAwait(false);
            semantic = semantic.Where(h => Allowed(h.Record)).ToList();
        }

        var metadata = mode == Mode.Semantic ? new List<Guid>() : MetadataMatches(query.Terms, snapshot.Meetings, filter);
        return new Result(HybridRanker.Fuse(keyword, semantic, metadata, limit), state);
    }

    public string Stats(CatalogSnapshot snapshot, SharedSettings settings)
    {
        try
        {
            var index = Synced(snapshot);
            var embedder = settings.SemanticSearchEnabled ? _embedderProvider() : null;
            var stats = index.Stats(embedder?.ModelID);
            var line = $"Suchindex: {stats.Meetings} Meetings, {stats.Chunks} Abschnitte.";
            if (embedder is not null)
                line += $" Inhaltliche Suche ({embedder.ModelID}): {stats.Embedded} von {stats.Chunks} Abschnitten berechnet.";
            else if (settings.SemanticSearchEnabled)
                line += " Inhaltliche Suche: Modell noch nicht geladen.";
            else
                line += " Inhaltliche Suche: aus.";
            return line;
        }
        catch (Exception ex)
        {
            return "Suchindex nicht verfügbar: " + ex.Message;
        }
    }

    /// <summary>Wartet auf eine laufende Hintergrund-Berechnung (für Tests und <c>--selftest</c>).</summary>
    public Task WaitForEmbeddingAsync()
    {
        lock (_gate) return _embedTask ?? Task.CompletedTask;
    }

    // MARK: Intern

    private SearchIndex Synced(CatalogSnapshot snapshot)
    {
        lock (_gate)
        {
            _index ??= new SearchIndex(_indexPath);
            // Auch neu abgleichen, wenn die App den Index geleert hat („Suchindex löschen“, Datei war offen).
            if (snapshot.Generation != _syncedGeneration || (snapshot.Meetings.Count > 0 && _index.Stats().Meetings == 0))
            {
                _index.Sync(snapshot.Meetings);
                _syncedGeneration = snapshot.Generation;
            }
            return _index;
        }
    }

    private async Task<(List<VectorHit>, SemanticState)> SemanticHitsAsync(string text, SearchIndex index, SharedSettings settings, int limit)
    {
        if (!settings.SemanticSearchEnabled) return (new List<VectorHit>(), SemanticState.Disabled);
        ITextEmbedder? embedder;
        try { embedder = _embedderProvider(); }
        catch (Exception ex) { return (new List<VectorHit>(), new SemanticState(SemanticKind.Failed, Error: ex.Message)); }
        if (embedder is null) return (new List<VectorHit>(), SemanticState.ModelMissing);
        StartEmbedding(index, embedder);
        try
        {
            var vector = await embedder.EmbedQueryAsync(text).ConfigureAwait(false);
            var hits = index.VectorSearch(vector, embedder.ModelID, limit);
            var stats = index.Stats(embedder.ModelID);
            var coverage = stats.Chunks == 0 ? 1 : (double)stats.Embedded / stats.Chunks;
            return (hits, new SemanticState(SemanticKind.Used, coverage));
        }
        catch (Exception ex)
        {
            return (new List<VectorHit>(), new SemanticState(SemanticKind.Failed, Error: ex.Message));
        }
    }

    /// <summary>Fehlende Vektoren im Hintergrund nachrechnen; die Suche wartet nicht darauf.</summary>
    private void StartEmbedding(SearchIndex index, ITextEmbedder embedder)
    {
        lock (_gate)
        {
            if (_embedTask is { IsCompleted: false }) return;
            _embedTask = Task.Run(async () =>
            {
                try { await index.EmbedPendingAsync(embedder).ConfigureAwait(false); }
                catch (Exception ex) { McpLog.Write("Vektoren nachrechnen fehlgeschlagen: " + ex.Message); }
            });
        }
    }

    public static bool Matches(StoredMeeting meeting, Filter filter)
    {
        var date = JsonCoding.ToUtc(meeting.Transcript.Date);
        if (filter.From is { } from && date < from) return false;
        if (filter.To is { } to && date >= to) return false;
        if (filter.Speaker is { } speaker && SpeakerIDs(speaker, meeting.Transcript).Count == 0) return false;
        return true;
    }

    /// <summary>Sprecher-IDs, deren ID oder Anzeigename den (gefalteten) Suchtext enthält.</summary>
    public static HashSet<string> SpeakerIDs(string folded, MeetingTranscript transcript) =>
        transcript.SpeakerIDs
            .Where(id => TextFolding.Fold(id).Contains(folded, StringComparison.Ordinal)
                         || TextFolding.Fold(transcript.DisplayName(id)).Contains(folded, StringComparison.Ordinal))
            .ToHashSet();

    /// <summary>Meetings, deren Titel oder Sprechernamen mindestens die Hälfte der Suchwörter enthalten.</summary>
    public static List<Guid> MetadataMatches(IReadOnlyList<string> terms, IEnumerable<StoredMeeting> meetings, Filter filter)
    {
        if (terms.Count == 0) return new List<Guid>();
        var needed = (terms.Count + 1) / 2;
        return meetings
            .Where(m => Matches(m, filter))
            .Select(m =>
            {
                var t = m.Transcript;
                var haystack = TextFolding.Fold(string.Join(" ", new[] { t.Title }.Concat(t.SpeakerIDs.Select(t.DisplayName))));
                var found = terms.Count(term => haystack.Contains(term, StringComparison.Ordinal));
                return (m.Id, Found: found, Date: JsonCoding.ToUtc(t.Date));
            })
            .Where(x => x.Found >= needed)
            .OrderByDescending(x => x.Found)
            .ThenByDescending(x => x.Date)
            .Select(x => x.Id)
            .ToList();
    }
}
