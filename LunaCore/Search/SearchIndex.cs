using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace LunaCore;

/// <summary>Ein Abschnitt, wie er im Index liegt.</summary>
public sealed record ChunkRecord(long Id, Guid MeetingID, ChunkKind Kind, TurnRange? Turns, double Start, double End,
                                 IReadOnlyList<string> SpeakerIDs, string Text);

/// <param name="Snippet">Ausschnitt mit **markierten** Fundstellen.</param>
public sealed record KeywordHit(ChunkRecord Record, string Snippet);

public sealed record VectorHit(ChunkRecord Record, float Similarity);

public sealed record SyncReport(int Added = 0, int Updated = 0, int Removed = 0, int Unchanged = 0)
{
    public bool HasChanges => Added + Updated + Removed > 0;
}

/// <param name="Embedded">Abschnitte mit Vektor des angefragten Modells.</param>
public sealed record IndexStats(int Meetings, int Chunks, int Embedded);

public sealed class SearchIndexException : Exception
{
    public SearchIndexException(string message) : base(message) { }
}

/// <summary>
/// Suchindex über die Meeting-Bibliothek: Stichwortsuche (SQLite FTS5, BM25) und Vektoren für die
/// inhaltliche Suche in einer Datei.
/// <list type="bullet">
/// <item><b>Abgeleitete Daten:</b> Der Index lässt sich jederzeit löschen; <see cref="Sync"/> baut ihn aus der Bibliothek neu.</item>
/// <item><b>Mehrere Prozesse:</b> App und luna-mcp öffnen dieselbe Datei. WAL-Modus und <c>busy_timeout</c> machen das
/// sicher; <see cref="Sync"/> ist idempotent (Änderungsmerkmal je Meeting), doppelte Arbeit ist harmlos.</item>
/// <item><b>Keine Namen im Index:</b> Titel und Sprechernamen löst der Aufrufer live aus <c>meeting.json</c> auf – so
/// macht Umbenennen den Index nie ungültig.</item>
/// <item><b>Vertrauliche Meetings</b> werden nie aufgenommen und beim nächsten <see cref="Sync"/> entfernt.</item>
/// </list>
/// Thread-sicher: Alle Zugriffe auf die Verbindung laufen nacheinander (wie der Swift-Actor).
/// </summary>
public sealed class SearchIndex : IDisposable
{
    internal const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    /// <summary>Zählt eigene Schreibvorgänge; zusammen mit <c>PRAGMA data_version</c> (fremde) entwertet er den Vektor-Cache.</summary>
    private long _localWrites;
    private VectorCache? _vectorCache;
    private int _isEmbedding;

    private sealed record VectorCache(string Model, long DataVersion, long LocalWrites, long[] Ids, int Dimensions, float[] Matrix);

    public string Path { get; }

    public SearchIndex(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        // Ohne Pooling: Beim Schließen ist die Datei wirklich frei (Löschen aus der App).
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        _db = new SqliteConnection(connectionString);
        _db.Open();
        try
        {
            PrepareSchema();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    // MARK: Abgleich

    /// <summary>Bringt den Stichwort-Teil auf den Stand von <paramref name="meetings"/> (ohne Vektoren – dafür <see cref="EmbedPendingAsync"/>).</summary>
    public SyncReport Sync(IEnumerable<StoredMeeting> meetings)
    {
        lock (_gate)
        {
            var existing = new Dictionary<string, string>();
            using (var select = Command("SELECT id, content_hash FROM meetings"))
            using (var reader = select.ExecuteReader())
                while (reader.Read()) existing.TryAdd(reader.GetString(0), reader.GetString(1));

            int added = 0, updated = 0, removed = 0, unchanged = 0;
            using var transaction = _db.BeginTransaction();
            using var insertMeeting = Command("INSERT INTO meetings(id, content_hash, indexed_at) VALUES ($id, $hash, $at)", transaction);
            using var insertChunk = Command("""
                INSERT INTO chunks(meeting_id, ordinal, kind, turn_from, turn_to, start_s, end_s, speaker_ids, text)
                VALUES ($meeting, $ordinal, $kind, $from, $to, $start, $end, $speakers, $text)
                """, transaction);
            var seen = new HashSet<string>();
            foreach (var meeting in meetings)
            {
                if (meeting.Transcript.Confidential) continue;
                var id = Key(meeting.Id);
                // Kopierte Meeting-Ordner tragen dieselbe ID – der erste gewinnt.
                if (!seen.Add(id)) continue;
                var chunks = Chunker.Chunks(meeting.Transcript);
                var hash = ContentHashOf(chunks);
                if (existing.TryGetValue(id, out var known))
                {
                    if (known == hash)
                    {
                        unchanged++;
                        continue;
                    }
                    DeleteMeeting(id, transaction);
                    updated++;
                }
                else
                {
                    added++;
                }
                Run(insertMeeting, ("$id", id), ("$hash", hash), ("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
                for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
                {
                    var chunk = chunks[ordinal];
                    Run(insertChunk, ("$meeting", id), ("$ordinal", ordinal), ("$kind", (int)chunk.Kind),
                        ("$from", chunk.Turns?.Lower), ("$to", chunk.Turns?.Upper),
                        ("$start", chunk.Start), ("$end", chunk.End),
                        ("$speakers", string.Join("\n", chunk.SpeakerIDs)), ("$text", chunk.Text));
                }
            }
            foreach (var id in existing.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                DeleteMeeting(id, transaction);
                removed++;
            }
            transaction.Commit();
            var report = new SyncReport(added, updated, removed, unchanged);
            if (report.HasChanges) _localWrites++;
            return report;
        }
    }

    /// <summary>
    /// Berechnet fehlende Vektoren für <see cref="ITextEmbedder.ModelID"/> in Stapeln. Läuft höchstens einmal gleichzeitig;
    /// Vektoren anderer Modelle werden verworfen. Gibt die Zahl neu eingebetteter Abschnitte zurück.
    /// </summary>
    /// <param name="progress">Nach jedem Stapel: (berechnet in diesem Lauf, noch offen).</param>
    public async Task<int> EmbedPendingAsync(ITextEmbedder embedder, int batchSize = 16, int maxChunks = int.MaxValue,
                                             Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _isEmbedding, 1) == 1) return 0;
        try
        {
            var model = embedder.ModelID;
            lock (_gate)
            {
                using var delete = Command("DELETE FROM vectors WHERE model <> $model");
                if (Run(delete, ("$model", model)) > 0) _localWrites++;
            }
            var done = 0;
            while (done < maxChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new List<(long Id, string Text)>();
                lock (_gate)
                {
                    using var pending = Command("""
                        SELECT c.id, c.text FROM chunks c LEFT JOIN vectors v ON v.chunk_id = c.id
                        WHERE v.chunk_id IS NULL ORDER BY c.id LIMIT $limit
                        """);
                    pending.Parameters.AddWithValue("$limit", Math.Min(batchSize, maxChunks - done));
                    using var reader = pending.ExecuteReader();
                    while (reader.Read()) batch.Add((reader.GetInt64(0), reader.GetString(1)));
                }
                if (batch.Count == 0) break;

                var vectors = await embedder.EmbedDocumentsAsync(batch.Select(b => b.Text).ToList(), cancellationToken).ConfigureAwait(false);
                if (vectors.Length != batch.Count)
                    throw new SearchIndexException($"Das Embedding-Modell lieferte {vectors.Length} statt {batch.Count} Vektoren.");

                int remaining;
                lock (_gate)
                {
                    using (var transaction = _db.BeginTransaction())
                    {
                        // Ein Abschnitt kann während des Rechnens verschwunden sein (anderer Prozess, Sync).
                        using var insert = Command("""
                            INSERT OR REPLACE INTO vectors(chunk_id, model, vector)
                            SELECT $id, $model, $vector WHERE EXISTS (SELECT 1 FROM chunks WHERE id = $id)
                            """, transaction);
                        for (var i = 0; i < batch.Count; i++)
                            Run(insert, ("$id", batch[i].Id), ("$model", model), ("$vector", VectorMath.ToBytes(VectorMath.Normalized(vectors[i]))));
                        transaction.Commit();
                    }
                    _localWrites++;
                    remaining = ScalarInt("SELECT COUNT(*) FROM chunks c LEFT JOIN vectors v ON v.chunk_id = c.id WHERE v.chunk_id IS NULL");
                }
                done += batch.Count;
                progress?.Invoke(done, remaining);
            }
            return done;
        }
        finally
        {
            Interlocked.Exchange(ref _isEmbedding, 0);
        }
    }

    // MARK: Suche

    /// <summary>Stichwortsuche: erst alle Bestandteile (UND); findet das zu wenig, ergänzt ODER-Treffer.</summary>
    public List<KeywordHit> KeywordSearch(KeywordQuery query, int limit)
    {
        if (query.All is not { } all) return new List<KeywordHit>();
        lock (_gate)
        {
            var hits = Match(all, limit);
            if (hits.Count < limit / 2 + 1 && query.Any is { } any)
            {
                var known = hits.Select(h => h.Record.Id).ToHashSet();
                hits.AddRange(Match(any, limit).Where(h => !known.Contains(h.Record.Id)));
            }
            return hits.Take(limit).ToList();
        }
    }

    /// <summary>
    /// Nächste Nachbarn per Kosinus-Ähnlichkeit (Vektoren sind normalisiert → Skalarprodukt).
    /// Leer, wenn für <paramref name="model"/> keine passenden Vektoren existieren.
    /// </summary>
    public List<VectorHit> VectorSearch(float[] query, string model, int limit)
    {
        lock (_gate)
        {
            var cache = Vectors(model);
            var q = VectorMath.Normalized(query);
            if (cache.Dimensions != q.Length || cache.Ids.Length == 0) return new List<VectorHit>();

            var scored = new (float Similarity, int Row)[cache.Ids.Length];
            for (var row = 0; row < cache.Ids.Length; row++)
            {
                var offset = row * cache.Dimensions;
                float sum = 0;
                for (var d = 0; d < cache.Dimensions; d++) sum += cache.Matrix[offset + d] * q[d];
                scored[row] = (sum, row);
            }
            var top = scored.OrderByDescending(s => s.Similarity).ThenBy(s => s.Row).Take(limit).ToList();
            var records = Records(top.Select(t => cache.Ids[t.Row]).ToList());
            return top
                .Where(t => records.ContainsKey(cache.Ids[t.Row]))
                .Select(t => new VectorHit(records[cache.Ids[t.Row]], t.Similarity))
                .ToList();
        }
    }

    public IndexStats Stats(string? model = null)
    {
        lock (_gate)
        {
            return new IndexStats(
                ScalarInt("SELECT COUNT(*) FROM meetings"),
                ScalarInt("SELECT COUNT(*) FROM chunks"),
                model is null ? 0 : ScalarInt("SELECT COUNT(*) FROM vectors WHERE model = $model", ("$model", model)));
        }
    }

    /// <summary>Leert den Index (alle Meetings, Abschnitte, Vektoren); er wird beim nächsten Abgleich neu aufgebaut.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            using (var command = Command("DELETE FROM vectors; DELETE FROM chunks; DELETE FROM meetings;", transaction))
                command.ExecuteNonQuery();
            transaction.Commit();
            _localWrites++;
        }
    }

    /// <summary>
    /// Löscht die Index-Datei. Hält ein anderer Prozess (luna-mcp) sie offen, lässt Windows das nicht zu – dann wird der
    /// Inhalt geleert, mit demselben Ergebnis: Der Index baut sich beim nächsten Abgleich neu auf.
    /// </summary>
    public static void Delete(string path)
    {
        try
        {
            foreach (var suffix in new[] { "-wal", "-shm", "" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path)) return;
            using var index = new SearchIndex(path);
            index.Reset();
        }
    }

    // MARK: Intern

    private List<KeywordHit> Match(string expression, int limit)
    {
        using var statement = Command("""
            SELECT c.id, c.meeting_id, c.kind, c.turn_from, c.turn_to, c.start_s, c.end_s, c.speaker_ids, c.text,
                   snippet(chunks_fts, 0, '**', '**', ' … ', 24)
            FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid
            WHERE chunks_fts MATCH $match ORDER BY bm25(chunks_fts) LIMIT $limit
            """);
        statement.Parameters.AddWithValue("$match", expression);
        statement.Parameters.AddWithValue("$limit", limit);
        var hits = new List<KeywordHit>();
        using var reader = statement.ExecuteReader();
        while (reader.Read())
            if (ReadRecord(reader) is { } record) hits.Add(new KeywordHit(record, reader.GetString(9)));
        return hits;
    }

    private Dictionary<long, ChunkRecord> Records(IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, ChunkRecord>();
        if (ids.Count == 0) return result;
        using var statement = Command(
            "SELECT id, meeting_id, kind, turn_from, turn_to, start_s, end_s, speaker_ids, text FROM chunks WHERE id IN ("
            + string.Join(",", ids.Select((_, i) => "$p" + i)) + ")");
        for (var i = 0; i < ids.Count; i++) statement.Parameters.AddWithValue("$p" + i, ids[i]);
        using var reader = statement.ExecuteReader();
        while (reader.Read())
            if (ReadRecord(reader) is { } record) result.TryAdd(record.Id, record);
        return result;
    }

    private static ChunkRecord? ReadRecord(SqliteDataReader row)
    {
        if (!Guid.TryParse(row.GetString(1), out var meetingID)) return null;
        TurnRange? turns = row.IsDBNull(3) || row.IsDBNull(4)
            ? null
            : new TurnRange(row.GetInt32(3), Math.Max(row.GetInt32(3), row.GetInt32(4)));
        var speakers = row.GetString(7).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var kind = row.GetInt32(2) == (int)ChunkKind.Summary ? ChunkKind.Summary : ChunkKind.Turns;
        return new ChunkRecord(row.GetInt64(0), meetingID, kind, turns, row.GetDouble(5), row.GetDouble(6), speakers, row.GetString(8));
    }

    private VectorCache Vectors(string model)
    {
        var dataVersion = ScalarLong("PRAGMA data_version");
        if (_vectorCache is { } cache && cache.Model == model && cache.DataVersion == dataVersion && cache.LocalWrites == _localWrites)
            return cache;

        var ids = new List<long>();
        var matrix = new List<float>();
        var dimensions = 0;
        using var statement = Command("SELECT chunk_id, vector FROM vectors WHERE model = $model ORDER BY chunk_id");
        statement.Parameters.AddWithValue("$model", model);
        using (var reader = statement.ExecuteReader())
        {
            while (reader.Read())
            {
                var vector = VectorMath.FromBytes((byte[])reader.GetValue(1));
                if (dimensions == 0) dimensions = vector.Length;
                if (vector.Length != dimensions || dimensions == 0) continue;
                ids.Add(reader.GetInt64(0));
                matrix.AddRange(vector);
            }
        }
        _vectorCache = new VectorCache(model, dataVersion, _localWrites, ids.ToArray(), dimensions, matrix.ToArray());
        return _vectorCache;
    }

    private void DeleteMeeting(string id, SqliteTransaction transaction)
    {
        using var command = Command("""
            DELETE FROM vectors WHERE chunk_id IN (SELECT id FROM chunks WHERE meeting_id = $id);
            DELETE FROM chunks WHERE meeting_id = $id;
            DELETE FROM meetings WHERE id = $id;
            """, transaction);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Änderungsmerkmal eines Meetings: Zerlegungsversion + alle Abschnitte.</summary>
    internal static string ContentHashOf(IReadOnlyList<Chunk> chunks)
    {
        var text = new StringBuilder("v" + Chunker.Version);
        foreach (var chunk in chunks)
        {
            text.Append('\u001E').Append((int)chunk.Kind).Append('|')
                .Append(chunk.Turns is { } t ? $"{t.Lower}-{t.Upper}" : "").Append('|')
                .Append(chunk.Start.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(chunk.End.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(string.Join(",", chunk.SpeakerIDs)).Append('|')
                .Append(chunk.Text);
        }
        return ContentHash.Hex(text.ToString());
    }

    /// <summary>Meeting-IDs stehen klein im Index (wie in den MCP-URIs).</summary>
    private static string Key(Guid id) => id.ToString("D");

    private void PrepareSchema()
    {
        Execute("PRAGMA busy_timeout = 5000");
        Execute("PRAGMA journal_mode = WAL");
        using var transaction = _db.BeginTransaction();
        if (ScalarLong("PRAGMA user_version", transaction) != SchemaVersion)
        {
            // Anderes Format (oder neu): Index ist abgeleitet, also einfach neu anlegen.
            using var command = Command($"""
                DROP TABLE IF EXISTS chunks_fts;
                DROP TABLE IF EXISTS vectors;
                DROP TABLE IF EXISTS chunks;
                DROP TABLE IF EXISTS meetings;
                CREATE TABLE meetings (
                    id TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    indexed_at REAL NOT NULL
                );
                CREATE TABLE chunks (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    meeting_id TEXT NOT NULL,
                    ordinal INTEGER NOT NULL,
                    kind INTEGER NOT NULL,
                    turn_from INTEGER,
                    turn_to INTEGER,
                    start_s REAL NOT NULL,
                    end_s REAL NOT NULL,
                    speaker_ids TEXT NOT NULL,
                    text TEXT NOT NULL
                );
                CREATE INDEX chunks_by_meeting ON chunks(meeting_id);
                CREATE VIRTUAL TABLE chunks_fts USING fts5(
                    text, content = 'chunks', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2'
                );
                CREATE TRIGGER chunks_after_insert AFTER INSERT ON chunks BEGIN
                    INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
                END;
                CREATE TRIGGER chunks_after_delete AFTER DELETE ON chunks BEGIN
                    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
                END;
                CREATE TABLE vectors (
                    chunk_id INTEGER PRIMARY KEY,
                    model TEXT NOT NULL,
                    vector BLOB NOT NULL
                );
                PRAGMA user_version = {SchemaVersion};
                """, transaction);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private static int Run(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        command.Parameters.Clear();
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private int ScalarInt(string sql, params (string Name, object? Value)[] values) => (int)ScalarLong(sql, null, values);

    private long ScalarLong(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] values)
    {
        using var command = Command(sql, transaction);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }
}
