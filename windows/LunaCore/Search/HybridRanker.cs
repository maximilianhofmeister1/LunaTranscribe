using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

/// <summary>Woher ein Treffer stammt.</summary>
public enum SearchSource
{
    Title,
    Keyword,
    Semantic,
}

public static class SearchSourceExtensions
{
    public static string Label(this SearchSource source) => source switch
    {
        SearchSource.Title => "Titel/Sprecher",
        SearchSource.Keyword => "Stichwort",
        _ => "inhaltlich",
    };

    /// <summary>Feste Reihenfolge wie in Swift (nach Bezeichnung sortiert).</summary>
    public static List<string> SortedLabels(this IEnumerable<SearchSource> sources) =>
        sources.Select(Label).OrderBy(l => l, StringComparer.Ordinal).ToList();
}

/// <summary>Ein Treffer der kombinierten Suche.</summary>
public sealed class SearchHit
{
    public Guid MeetingID { get; init; }
    /// <summary>Fundstelle im Meeting; null, wenn nur Titel oder Sprechername passen.</summary>
    public ChunkRecord? Chunk { get; init; }
    /// <summary>Ausschnitt aus der Stichwortsuche (mit **Markierung**), falls vorhanden.</summary>
    public string? Snippet { get; set; }
    public HashSet<SearchSource> Sources { get; } = new();
    public double Score { get; set; }
}

/// <summary>
/// Führt Stichwort-, Vektor- und Titel-Treffer per <b>Reciprocal Rank Fusion</b> zusammen:
/// Score = Σ 1 / (k + Rang). RRF braucht keine Kalibrierung der unvergleichbaren Einzelscores
/// (BM25 vs. Kosinus) und ist deshalb das übliche Verfahren für hybride Suche.
/// </summary>
public static class HybridRanker
{
    /// <summary>Üblicher Wert aus der Literatur (Cormack et al., 2009); dämpft den Vorsprung der Spitzenplätze.</summary>
    public const double K = 60.0;

    /// <param name="metadata">Meetings, deren Titel oder Sprechernamen passen, beste zuerst.</param>
    /// <param name="perMeeting">Höchstens so viele Treffer je Meeting, damit ein langes Meeting nicht alles belegt.</param>
    public static List<SearchHit> Fuse(IReadOnlyList<KeywordHit> keyword, IReadOnlyList<VectorHit> semantic,
                                       IReadOnlyList<Guid> metadata, int limit, int perMeeting = 3)
    {
        var hits = new Dictionary<string, SearchHit>();

        void Add(string key, Guid meetingID, ChunkRecord? chunk, string? snippet, SearchSource source, int rank)
        {
            var contribution = 1 / (K + rank + 1);
            if (!hits.TryGetValue(key, out var hit))
            {
                hit = new SearchHit { MeetingID = meetingID, Chunk = chunk, Snippet = snippet };
                hits[key] = hit;
            }
            else if (hit.Snippet is null)
            {
                hit.Snippet = snippet;
            }
            hit.Score += contribution;
            hit.Sources.Add(source);
        }

        for (var rank = 0; rank < keyword.Count; rank++)
            Add("c" + keyword[rank].Record.Id, keyword[rank].Record.MeetingID, keyword[rank].Record, keyword[rank].Snippet,
                SearchSource.Keyword, rank);
        for (var rank = 0; rank < semantic.Count; rank++)
            Add("c" + semantic[rank].Record.Id, semantic[rank].Record.MeetingID, semantic[rank].Record, null,
                SearchSource.Semantic, rank);
        for (var rank = 0; rank < metadata.Count; rank++)
            Add("m" + metadata[rank].ToString("D"), metadata[rank], null, null, SearchSource.Title, rank);

        var perMeetingCount = new Dictionary<Guid, int>();
        return hits.Values
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Chunk?.Id ?? 0)
            .ThenBy(h => h.MeetingID)
            .Where(hit =>
            {
                perMeetingCount.TryGetValue(hit.MeetingID, out var count);
                if (count >= perMeeting) return false;
                perMeetingCount[hit.MeetingID] = count + 1;
                return true;
            })
            .Take(limit)
            .ToList();
    }
}
