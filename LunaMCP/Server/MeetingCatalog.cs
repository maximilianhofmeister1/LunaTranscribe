using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LunaCore;

namespace LunaMCP.Server;

/// <summary>Stand des Katalogs zu einem Zeitpunkt.</summary>
/// <param name="Meetings">Neueste zuerst, ohne vertrauliche.</param>
/// <param name="Generation">Steigt, wenn sich etwas geändert hat.</param>
internal sealed record CatalogSnapshot(IReadOnlyList<StoredMeeting> Meetings, int Generation, string LibraryFolder, bool LibraryReachable)
{
    public StoredMeeting? Meeting(Guid id) => Meetings.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Findet ein Meeting über die volle ID oder ein eindeutiges Präfix ab 6 Zeichen (wie im Ordnernamen).
    /// Vertrauliche Meetings gelten als nicht vorhanden – ihre Existenz wird nicht verraten.
    /// </summary>
    /// <exception cref="ToolError"/>
    public StoredMeeting Resolve(string reference)
    {
        var needle = reference.Trim().ToLowerInvariant();
        if (Guid.TryParse(needle, out var id) && Meeting(id) is { } exact) return exact;
        if (needle.Length < 6)
            throw new ToolError($"„{reference}“ ist zu kurz – gib mindestens die 6 Zeichen der Meeting-ID an (aus list_meetings oder search_meetings).");
        var matches = Meetings.Where(m => Rendering.IdString(m.Id).StartsWith(needle, StringComparison.Ordinal)).ToList();
        switch (matches.Count)
        {
            case 1:
                return matches[0];
            case 0:
                throw new ToolError($"Kein Meeting mit der ID „{reference}“. Nutze list_meetings oder search_meetings, um die ID zu finden.");
            default:
                var candidates = matches.Take(5).Select(m => $"{Rendering.IdString(m.Id)} („{m.Transcript.Title}“)");
                throw new ToolError($"Die ID „{reference}“ ist mehrdeutig: {string.Join(", ", candidates)}. Gib mehr Zeichen an.");
        }
    }
}

/// <summary>
/// Die Meetings, die Claude sehen darf – aus Meeting-Ordner und Rettungsordner, <b>ohne vertrauliche</b>.
/// Liest nur geänderte <c>meeting.json</c> neu (Änderungszeit + Größe); <c>Generation</c> steigt, wenn sich etwas
/// geändert hat – daran hängt der Abgleich des Suchindex und die <c>list_changed</c>-Meldung.
/// </summary>
internal sealed class MeetingCatalog
{
    private sealed record CacheEntry(string Stamp, StoredMeeting? Meeting);

    private readonly object _gate = new();
    private Dictionary<string, CacheEntry> _cache = new();
    private string _signature = "";
    private int _generation;

    public CatalogSnapshot Snapshot(string libraryFolder, string unsavedFolder)
    {
        lock (_gate)
        {
            var reachable = Directory.Exists(libraryFolder);
            var folders = MeetingLibrary.MeetingFolders(libraryFolder).Select(f => (Folder: f, Unsaved: false))
                .Concat(MeetingLibrary.MeetingFolders(unsavedFolder).Select(f => (Folder: f, Unsaved: true)));

            var fresh = new Dictionary<string, CacheEntry>();
            foreach (var (folder, unsaved) in folders)
            {
                var stamp = Stamp(folder);
                if (_cache.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
                {
                    fresh[folder] = cached;
                    continue;
                }
                StoredMeeting? meeting;
                try { meeting = MeetingLibrary.Read(folder, unsaved); }
                catch (Exception) { meeting = null; }
                fresh[folder] = new CacheEntry(stamp, meeting);
            }
            _cache = fresh;

            var keys = fresh.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var signature = string.Join("\n", keys.Select(k => $"{k}={fresh[k].Stamp}"));
            if (signature != _signature)
            {
                _signature = signature;
                _generation++;
            }

            // Doppelte IDs (kopierter Ordner): der zuerst gefundene gewinnt, stabil nach Pfad.
            var seen = new HashSet<Guid>();
            var meetings = keys
                .Select(k => fresh[k].Meeting)
                .OfType<StoredMeeting>()
                .Where(m => !m.Transcript.Confidential && seen.Add(m.Id))
                .OrderByDescending(m => JsonCoding.ToUtc(m.Transcript.Date))
                .ToList();
            return new CatalogSnapshot(meetings, _generation, libraryFolder, reachable);
        }
    }

    private static string Stamp(string folder)
    {
        var file = new FileInfo(Path.Combine(folder, MeetingLibrary.TranscriptFileName));
        var audio = File.Exists(Path.Combine(folder, MeetingLibrary.AudioFileName));
        try
        {
            return $"{file.LastWriteTimeUtc.Ticks}|{file.Length}|{audio}";
        }
        catch (IOException)
        {
            return $"0|0|{audio}";
        }
    }
}
