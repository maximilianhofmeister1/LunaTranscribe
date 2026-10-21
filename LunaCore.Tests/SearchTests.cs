using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using Xunit;

namespace LunaCore.Tests;

public class ChunkerTests
{
    [Fact(DisplayName = "Kurze Beiträge landen zusammen in einem Abschnitt")]
    public void ShortMeetingIsOneChunk()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Hallo."), ("Sprecher 2", "Hi."), ("Sprecher 1", "Los geht's.") });
        var chunks = Chunker.Chunks(meeting);
        Assert.Single(chunks);
        Assert.Equal(new TurnRange(0, 2), chunks[0].Turns);
        Assert.Equal(new[] { "Sprecher 1", "Sprecher 2" }, chunks[0].SpeakerIDs);
    }

    [Fact(DisplayName = "Lange Meetings werden mit Überlappung geteilt, nichts geht verloren")]
    public void LongMeetingOverlaps()
    {
        var sentence = string.Join(" ", Enumerable.Repeat("wort", 70));
        var meeting = Support.Meeting(Enumerable.Range(0, 10).Select(i => ($"Sprecher {i % 2 + 1}", sentence)));
        var chunks = Chunker.Chunks(meeting);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(Chunker.WordCount(c.Text) <= Chunker.TargetWords + 70));
        var covered = chunks.SelectMany(c => c.Turns!.Value.Indices).ToHashSet();
        Assert.True(covered.SetEquals(Enumerable.Range(0, 10)));
        Assert.Equal(9, chunks[^1].Turns!.Value.Upper);
    }

    [Fact(DisplayName = "Ein überlanger Beitrag wird an Satzgrenzen geteilt")]
    public void SplitsLongTurn()
    {
        var sentence = string.Join(" ", Enumerable.Repeat("wort", 30)) + ". ";
        var meeting = Support.Meeting(new[] { ("Sprecher 1", string.Concat(Enumerable.Repeat(sentence, 20))) });
        var chunks = Chunker.Chunks(meeting);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.Equal(new TurnRange(0, 0), c.Turns));
    }

    [Fact(DisplayName = "Zusammenfassung wird nach Überschriften zerlegt")]
    public void SummarySections()
    {
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Text") }, summary: "## Kurzfassung\nA\n\n## Aufgaben\n- [ ] B");
        var summary = Chunker.Chunks(meeting).Where(c => c.Kind == ChunkKind.Summary).Select(c => c.Text);
        Assert.Equal(new[] { "## Kurzfassung\nA", "## Aufgaben\n- [ ] B" }, summary);
    }
}

public class KeywordQueryTests
{
    [Fact(DisplayName = "Füllwörter fallen weg, lange Wörter suchen als Präfix")]
    public void NaturalQuestion()
    {
        var query = new KeywordQuery("Wo haben wir über die Preise gesprochen?");
        Assert.Equal("\"preise\"*", query.All);
        Assert.Null(query.Any);
    }

    [Fact(DisplayName = "Phrasen und Umlaute")]
    public void Phrases()
    {
        var query = new KeywordQuery("„nächstes Quartal“ Müller");
        Assert.Equal("\"nachstes quartal\" AND \"muller\"*", query.All);
        Assert.Equal("\"nachstes quartal\" OR \"muller\"*", query.Any);
    }

    [Fact(DisplayName = "Wörterbuch-Varianten werden gemeinsam gesucht")]
    public void VocabularyExpansion()
    {
        var vocabulary = new[] { new VocabularyEntry("LunaTranscribe", new[] { "Luna Transcribe", "Luna transkribe" }) };
        var query = new KeywordQuery("Luna Transcribe Release", vocabulary);
        Assert.Equal("(\"luna transcribe\" OR \"luna transkribe\" OR \"lunatranscribe\") AND \"release\"*", query.All);
    }

    [Fact(DisplayName = "ß bleibt erhalten wie im FTS-Tokenizer")]
    public void Eszett() => Assert.Equal("\"straße\"*", new KeywordQuery("Straße").All);

    [Fact(DisplayName = "Leere Anfrage und nur Füllwörter")]
    public void Empty()
    {
        Assert.True(new KeywordQuery("").IsEmpty);
        Assert.True(new KeywordQuery("wo haben wir").IsEmpty);
    }
}

public class SearchIndexTests
{
    private static List<StoredMeeting> Library()
    {
        var budget = Support.Meeting(new[]
        {
            ("Sprecher 1", "Dann lass uns mit dem Budget anfangen."),
            ("Sprecher 2", "Wir müssen die Preise im nächsten Quartal erhöhen."),
        }, title: "Planung Q4", summary: "## Aufgaben\n- [ ] Tom: Angebot einholen");
        var retro = Support.Meeting(new[]
        {
            ("Sprecher 1", "Das Deployment am Freitag war holprig."),
            ("Sprecher 2", "Wir sollten teurer werden, ehrlich gesagt."),
        }, title: "Retro");
        var secret = Support.Meeting(new[] { ("Sprecher 1", "Budget und Gehalt.") }, title: "Personalgespräch", confidential: true);
        return new[] { budget, retro, secret }
            .Select(t => new StoredMeeting(t, Path.Combine(Path.GetTempPath(), t.Id.ToString()), "r", false, false))
            .ToList();
    }

    private static StoredMeeting With(StoredMeeting meeting, Action<MeetingTranscript> change, string revision = "r2")
    {
        var transcript = meeting.Transcript.Clone();
        change(transcript);
        return meeting with { Transcript = transcript, Revision = revision };
    }

    [Fact(DisplayName = "Abgleich ist idempotent und lässt vertrauliche Meetings weg")]
    public void SyncIdempotent()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        var meetings = Library();
        Assert.Equal(new SyncReport(Added: 2), index.Sync(meetings));
        Assert.Equal(new SyncReport(Unchanged: 2), index.Sync(meetings));

        var changed = With(meetings[0], t => t.Turns[0] = t.Turns[0] with { Text = "Neuer Text" });
        Assert.Equal(new SyncReport(Updated: 1, Removed: 1), index.Sync(new[] { changed }));
    }

    [Fact(DisplayName = "Umbenennen (Titel, Sprecher) ändert den Index nicht")]
    public void RenameDoesNotReindex()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        var meetings = Library();
        index.Sync(meetings);
        meetings[0] = With(meetings[0], t =>
        {
            t.Title = "Anders";
            t.SpeakerNames["Sprecher 1"] = "Anna";
        });
        Assert.False(index.Sync(meetings).HasChanges);
    }

    [Fact(DisplayName = "Stichwortsuche mit Präfix, Umlaut und Fundstellen-Markierung")]
    public void KeywordSearch()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        var meetings = Library();
        index.Sync(meetings);

        var hits = index.KeywordSearch(new KeywordQuery("Preis erhöhen"), 5);
        Assert.Single(hits);
        Assert.Equal(meetings[0].Id, hits[0].Record.MeetingID);
        Assert.Contains("**Preise**", hits[0].Snippet);

        // Vertrauliches Meeting nie im Index.
        Assert.Empty(index.KeywordSearch(new KeywordQuery("Gehalt"), 5));
        // Zusammenfassung ist durchsuchbar.
        Assert.Equal(ChunkKind.Summary, index.KeywordSearch(new KeywordQuery("Angebot"), 5).First().Record.Kind);
    }

    [Fact(DisplayName = "ODER-Rückfallebene, wenn UND nichts findet")]
    public void OrFallback()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        index.Sync(Library());
        Assert.Equal(2, index.KeywordSearch(new KeywordQuery("Deployment Budget"), 5).Count);
    }

    [Fact(DisplayName = "Vektorsuche und Nachberechnen fehlender Vektoren")]
    public async Task VectorSearch()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        index.Sync(Library());
        var embedder = new FakeEmbedder();
        Assert.Equal(3, await index.EmbedPendingAsync(embedder));   // 2 Meetings à 1 Abschnitt + 1 Zusammenfassungsabschnitt
        Assert.Equal(0, await index.EmbedPendingAsync(embedder));
        Assert.Equal(new IndexStats(2, 3, 3), index.Stats(embedder.ModelID));

        var query = await embedder.EmbedQueryAsync("kosten");
        var hits = index.VectorSearch(query, embedder.ModelID, 1);
        Assert.Single(hits);

        // Anderes Modell: alte Vektoren weg, neu rechnen.
        var other = new FakeEmbedder { ModelID = "fake-2" };
        Assert.Equal(3, await index.EmbedPendingAsync(other));
        Assert.Empty(index.VectorSearch(query, embedder.ModelID, 3));
    }

    [Fact(DisplayName = "Fusion: gemeinsame Treffer vorne, höchstens n je Meeting")]
    public async Task Fusion()
    {
        using var folder = new TempFolder();
        using var index = new SearchIndex(Path.Combine(folder.Path, "i.sqlite"));
        var meetings = Library();
        index.Sync(meetings);
        var embedder = new FakeEmbedder();
        await index.EmbedPendingAsync(embedder);

        var keyword = index.KeywordSearch(new KeywordQuery("Preise"), 10);
        var semantic = index.VectorSearch(await embedder.EmbedQueryAsync("Preise"), embedder.ModelID, 10);
        var fused = HybridRanker.Fuse(keyword, semantic, new[] { meetings[1].Id }, limit: 10, perMeeting: 1);
        Assert.True(fused[0].Sources.SetEquals(new[] { SearchSource.Keyword, SearchSource.Semantic }));
        Assert.Equal(fused.Count, fused.Select(h => h.MeetingID).Distinct().Count());
        Assert.Equal(new[] { "Stichwort", "inhaltlich" }, fused[0].Sources.SortedLabels());
    }

    [Fact(DisplayName = "Index überlebt Schließen und Wiederöffnen (zweiter Prozess)")]
    public void Reopen()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "i.sqlite");
        var meetings = Library();
        using (var index = new SearchIndex(path)) index.Sync(meetings);
        using var again = new SearchIndex(path);
        Assert.Equal(2, again.Stats().Meetings);
        using var other = new SearchIndex(path);   // parallele Verbindung
        Assert.False(other.Sync(meetings).HasChanges);
    }

    [Fact(DisplayName = "Vektor-Cache merkt Schreibvorgänge einer anderen Verbindung")]
    public async Task CacheInvalidatedByOtherConnection()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "i.sqlite");
        using var reader = new SearchIndex(path);
        using var writer = new SearchIndex(path);
        var embedder = new FakeEmbedder();
        writer.Sync(Library());
        var query = await embedder.EmbedQueryAsync("Preise");
        Assert.Empty(reader.VectorSearch(query, embedder.ModelID, 5));   // Cache leer gefüllt
        await writer.EmbedPendingAsync(embedder);
        Assert.NotEmpty(reader.VectorSearch(query, embedder.ModelID, 5)); // data_version hat sich geändert
    }

    [Fact(DisplayName = "Löschen bei offener Datei leert den Index stattdessen")]
    public void DeleteWhileOpen()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "i.sqlite");
        using (var index = new SearchIndex(path)) index.Sync(Library());
        SearchIndex.Delete(path);
        Assert.False(File.Exists(path));

        using var open = new SearchIndex(path);
        open.Sync(Library());
        SearchIndex.Delete(path);   // „anderer Prozess“ hält die Datei offen
        Assert.Equal(new IndexStats(0, 0, 0), open.Stats("x"));
    }
}

public class XlmRobertaTokenizerTests
{
    /// <summary>Kleines Unigram-Vokabular im Format von tokenizer.json.</summary>
    private static XlmRobertaTokenizer Small()
    {
        const string json = """
        {
          "added_tokens": [
            {"id": 0, "content": "<s>", "special": true}, {"id": 1, "content": "<pad>", "special": true},
            {"id": 2, "content": "</s>", "special": true}, {"id": 3, "content": "<unk>", "special": true}
          ],
          "normalizer": {"type": "Sequence", "normalizers": [{"type": "Replace", "pattern": {"Regex": " {2,}"}, "content": " "}]},
          "pre_tokenizer": {"type": "Metaspace", "replacement": "▁", "add_prefix_space": true},
          "model": {"type": "Unigram", "unk_id": 3, "vocab": [
            ["<s>", 0.0], ["<pad>", 0.0], ["</s>", 0.0], ["<unk>", 0.0],
            ["▁", -2.0], ["▁hallo", -3.0], ["▁hal", -4.0], ["lo", -4.0], ["▁welt", -3.5], ["w", -6.0], ["e", -6.0],
            ["l", -6.0], ["t", -6.0], ["▁we", -5.0], ["lt", -5.0]
          ]}
        }
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return XlmRobertaTokenizer.FromJson(doc.RootElement);
    }

    [Fact(DisplayName = "Unigram wählt die wahrscheinlichste Zerlegung, Spezial-Token drumherum")]
    public void Viterbi()
    {
        var tokenizer = Small();
        Assert.Equal(new[] { "▁hallo", "▁welt" }, tokenizer.Tokenize("hallo  welt"));
        Assert.Equal(new[] { 0, 5, 8, 2 }, tokenizer.Encode("hallo welt"));
    }

    [Fact(DisplayName = "Unbekannte Zeichen werden zu einem <unk> verschmolzen")]
    public void UnknownFused()
    {
        var tokenizer = Small();
        Assert.Equal(new[] { 0, 4, 3, 2 }, tokenizer.Encode("äöü"));
        Assert.Equal(new[] { 0, 5, 3, 2 }, tokenizer.Encode("hallo😀😀"));
    }

    [Fact(DisplayName = "Kürzen auf maxTokens behält </s>")]
    public void Truncation()
    {
        var ids = Small().Encode(string.Join(" ", Enumerable.Repeat("hallo", 100)), maxTokens: 10);
        Assert.Equal(10, ids.Length);
        Assert.Equal(0, ids[0]);
        Assert.Equal(2, ids[^1]);
    }

    /// <summary>Gegen den echten Tokenizer von multilingual-e5-small, falls das Modell lokal liegt.</summary>
    [Fact(DisplayName = "Echter E5-Tokenizer liefert die bekannten XLM-R-IDs")]
    public void RealTokenizer()
    {
        var path = Environment.GetEnvironmentVariable("LUNA_E5_TOKENIZER")
                   ?? EmbeddingModelKind.MultilingualE5Small.LocalPath("tokenizer.json");
        if (!File.Exists(path)) return;   // Modell nicht geladen – nur die synthetischen Tests laufen
        var tokenizer = XlmRobertaTokenizer.Load(path);
        Assert.Equal(new[] { 0, 35378, 8999, 2 }, tokenizer.Encode("Hello world"));
        Assert.Equal(new[] { "▁que", "ry", ":", "▁Hello" }, tokenizer.Tokenize("query: Hello"));
        Assert.Equal(new[] { 0, 41, 1294, 12, 35378, 2 }, tokenizer.Encode("query: Hello"));
        // Vollbreite Zeichen werden per NFKC normalisiert.
        Assert.Equal(tokenizer.Encode("ABC"), tokenizer.Encode("ＡＢＣ"));
    }
}

public class ActivityUndoTests
{
    [Fact(DisplayName = "Rückgängig für Titel und Sprecher, nur solange der Wert unverändert ist")]
    public void Undo()
    {
        using var folder = new TempFolder();
        var meeting = Support.Meeting(new[] { ("Sprecher 1", "Hallo"), ("Sprecher 2", "Hi") });
        var stored = MeetingLibrary.Read(MeetingLibrary.Write(meeting, folder.Path, null));
        stored = MeetingLibrary.Update(stored.Folder, stored.Revision, t =>
        {
            t.Title = "Budget Q4";
            t.SpeakerNames["Sprecher 2"] = "Tom";
        });

        var renameTitle = new ActivityEntry("claude-code", ActivityAction.RenameMeeting, new[] { meeting.Id }, oldValue: "Planung Q4", newValue: "Budget Q4");
        var renameSpeaker = new ActivityEntry("claude-code", ActivityAction.RenameSpeaker, new[] { meeting.Id }, speakerID: "Sprecher 2", oldValue: "Sprecher 2", newValue: "Tom");
        Assert.True(ActivityUndo.CanUndo(renameTitle));
        Assert.False(ActivityUndo.CanUndo(new ActivityEntry("x", ActivityAction.Delete, new[] { meeting.Id })));

        var undone = ActivityUndo.Apply(renameTitle, new[] { folder.Path });
        Assert.Equal("Planung Q4", undone.Transcript.Title);
        undone = ActivityUndo.Apply(renameSpeaker, new[] { folder.Path });
        Assert.False(undone.Transcript.SpeakerNames.ContainsKey("Sprecher 2"));

        // Zweites Mal: Wert entspricht nicht mehr Claudes Änderung.
        Assert.Throws<UndoChangedSinceException>(() => ActivityUndo.Apply(renameTitle, new[] { folder.Path }));
    }
}

/// <summary>
/// Deterministischer Ersatz für das Embedding-Modell: Bag-of-Words über gefaltete Wortstämme (5 Zeichen),
/// verteilt auf 64 Dimensionen. Genug, um Fusion und Vektorsuche zu prüfen, ohne ein Modell zu laden.
/// </summary>
internal sealed class FakeEmbedder : ITextEmbedder
{
    public string ModelID { get; init; } = "fake-bow-64";
    /// <summary>Synonyme, damit „inhaltliche“ Treffer ohne gemeinsames Wort prüfbar sind.</summary>
    public Dictionary<string, string> Synonyms { get; init; } = new() { ["teurer"] = "preis", ["kosten"] = "preis" };

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(Vector).ToArray());

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(Vector(text));

    private float[] Vector(string text)
    {
        var v = new float[64];
        foreach (var word in TextFolding.Fold(text).Split(c => !char.IsLetter(c)))
        {
            var stem = Synonyms.TryGetValue(word, out var s) ? s : word.Length > 5 ? word[..5] : word;
            ulong hash = 1469598103934665603;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(stem)) hash = unchecked((hash ^ b) * 1099511628211);
            v[(int)(hash % 64)] += 1;
        }
        return v;
    }
}

internal static class StringSplitExtensions
{
    public static IEnumerable<string> Split(this string text, Func<char, bool> separator)
    {
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (separator(text[i]))
            {
                if (start >= 0) yield return text[start..i];
                start = -1;
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) yield return text[start..];
    }
}
