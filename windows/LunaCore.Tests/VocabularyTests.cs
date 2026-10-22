using System;
using System.Collections.Generic;
using System.Linq;
using LunaCore;
using Xunit;
using Candidate = LunaCore.CorrectionLearner.Candidate;

namespace LunaCore.Tests;

public class CustomVocabularyTests
{
    /// <summary>„Begriff=Variante1,Variante2;Begriff2“ → Einträge (Format von <c>--vocab-selftest --terms</c>).</summary>
    private static CustomVocabulary Parse(string terms) => new(terms.Split(';').Select(part =>
    {
        var pieces = part.Split('=', 2);
        return new VocabularyEntry(pieces[0], pieces.Length > 1 ? pieces[1].Split(',') : Array.Empty<string>());
    }));

    [Theory(DisplayName = "Varianten werden an Wortgrenzen ersetzt")]
    [InlineData("Ich teste Luna Transcribe mit kubernetes", "Ich teste LunaTranscribe mit Kubernetes")]
    [InlineData("luna   transcribe ist da", "LunaTranscribe ist da")]
    [InlineData("LUNA TRANSKRIBE.", "LunaTranscribe.")]
    [InlineData("Lunatranscribe und Luna", "LunaTranscribe und Luna")]
    [InlineData("LunaTranscribe bleibt", "LunaTranscribe bleibt")]
    [InlineData("Kuberneteskluster bleibt", "Kuberneteskluster bleibt")]
    [InlineData("Wir mögen c++ und .net", "Wir mögen C++ und .NET")]
    [InlineData("Die Grüße an Müller", "Die Grüße an Müller")]
    [InlineData("Herr mueller und Herr Müllerx", "Herr Müller und Herr Müllerx")]
    [InlineData("", "")]
    public void Correct(string input, string expected)
    {
        var vocab = Parse("LunaTranscribe=Luna Transcribe,Luna transkribe;Kubernetes;C++;.NET;Müller=mueller");
        Assert.Equal(expected, vocab.Correct(input));
    }

    [Fact(DisplayName = "Längste Variante zuerst, erster Eintrag gewinnt bei doppelten Varianten")]
    public void LongestFirstAndFirstWins()
    {
        var vocab = Parse("Luna=luna;LunaTranscribe=luna transcribe;Erster=dings;Zweiter=Dings");
        Assert.Equal("LunaTranscribe und Luna", vocab.Correct("luna transcribe und luna"));
        Assert.Equal("Erster", vocab.Correct("DINGS"));
    }

    [Fact(DisplayName = "Bereinigung: leere Begriffe und Varianten fallen weg")]
    public void Cleaning()
    {
        var vocab = new CustomVocabulary(new[]
        {
            new VocabularyEntry("  ", new[] { "x" }),
            new VocabularyEntry(" weclapp ", new[] { " Weglap ", "", "  " }),
        });
        var entry = Assert.Single(vocab.Entries);
        Assert.Equal("weclapp", entry.Term);
        Assert.Equal(new[] { "Weglap" }, entry.Aliases);
        Assert.Equal("Export aus weclapp", vocab.Correct("Export aus Weglap"));
        Assert.True(CustomVocabulary.Disabled.IsEmpty);
        Assert.Equal("unverändert", CustomVocabulary.Disabled.Correct("unverändert"));
        Assert.Equal(vocab, new CustomVocabulary(vocab.Entries));
    }
}

public class CorrectionLearnerTests
{
    /// <summary>Die eingebauten Fälle von <c>--learn-selftest</c> der macOS-Version.</summary>
    public static IEnumerable<object[]> SelfTestCases() => new[]
    {
        Case("Ich schicke dir den Export aus Weglap.", "Ich schicke dir den Export aus weclapp.", ("Weglap", "weclapp")),
        Case("Heute habe ich Luna Transcribe getestet", "Heute habe ich LunaTranscribe getestet", ("Luna Transcribe", "LunaTranscribe")),
        Case("Wir deployen auf Cubernetes.", "Wir deployen auf Kubernetes.", ("Cubernetes", "Kubernetes")),
        Case("Das Meeting mit Maximillian ist morgen", "Das Meeting mit Maximilian ist morgen", ("Maximillian", "Maximilian")),
        // Umformulierung: kein Verhörer
        Case("Das finde ich gut.", "Das finde ich hervorragend."),
        // Funktionswort getauscht
        Case("Ich gehe in die Stadt.", "Ich gehe in der Stadt."),
        // Satzanfang groß geschrieben
        Case("morgen passt es mir.", "Morgen passt es mir."),
        // Nur Zeichensetzung
        Case("Ja das passt", "Ja, das passt."),
        // Großschreibung mitten im Satz: Marke
        Case("Mein neues Iphone ist da", "Mein neues iPhone ist da", ("Iphone", "iPhone")),
        // Beugung eines normalen Worts: Grammatik, kein Verhörer
        Case("Dann klappen wir das Buch zu", "Dann klappten wir das Buch zu"),
        // Ganzer Satz umgeschrieben: zu lang für einen Verhörer
        Case("Kannst du das bitte bis morgen erledigen", "Schaffst du es vielleicht noch heute Abend"),
    };

    private static object[] Case(string before, string after, params (string Variant, string Term)[] expected) =>
        new object[] { before, after, expected.Select(e => $"{e.Variant} → {e.Term}").ToArray() };

    [Theory(DisplayName = "Eingebaute Fälle des Lern-Selbsttests")]
    [MemberData(nameof(SelfTestCases))]
    public void SelfTest(string before, string after, string[] expected)
    {
        var found = CorrectionLearner.Candidates(before, after).Select(c => $"{c.Variant} → {c.Term}");
        Assert.Equal(expected, found);
    }

    [Fact(DisplayName = "Großschreibung nach Satzende zählt als Satzanfang, doppelte Kandidaten nur einmal")]
    public void SentenceStartAndDuplicates()
    {
        Assert.Empty(CorrectionLearner.Candidates("Gut. morgen geht es los.", "Gut. Morgen geht es los."));
        Assert.Equal(new[] { new Candidate("Weglap", "weclapp") },
                     CorrectionLearner.Candidates("Weglap hier, Weglap da und dort", "weclapp hier, weclapp da und dort"));
    }

    [Fact(DisplayName = "Mehr als drei Wörter am Stück und reine Einfügungen sind keine Kandidaten")]
    public void HunkLimits()
    {
        Assert.Empty(CorrectionLearner.Candidates("a b c d e f g h i j", "a b c d e f g h i j k"));
        Assert.Empty(CorrectionLearner.Candidates("eins zwei drei vier fünf sechs sieben acht neun zehn elf",
                                                   "eins Zwo Drei Vier Fünf sechs sieben acht neun zehn elf"));
    }

    [Fact(DisplayName = "Hilfsfunktionen: Ähnlichkeit, Klangform, Beugung, Tokens")]
    public void Helpers()
    {
        Assert.Equal(1, CorrectionLearner.Similarity("", ""));
        Assert.Equal(0, CorrectionLearner.Similarity("abc", ""));
        Assert.Equal(0.75, CorrectionLearner.Similarity("abcd", "abce"));
        Assert.Equal(CorrectionLearner.PhoneticKey("Weglap"), CorrectionLearner.PhoneticKey("weclapp"));
        Assert.Equal("sule", CorrectionLearner.PhoneticKey("Schule"));
        Assert.True(CorrectionLearner.IsInflection("klappen", "klappten"));
        Assert.False(CorrectionLearner.IsInflection("Klappen", "klappten"));
        Assert.False(CorrectionLearner.IsLikelyMishearing("12", "13", atSentenceStart: false));

        var tokens = CorrectionLearner.Tokens("„Hallo“, sagt C++! (ok) – #tag:");
        Assert.Equal(new[] { "Hallo", "sagt", "C++", "ok", "#tag" }, tokens.Select(t => t.Word));
        Assert.Equal(new[] { false, false, true, false, true }, tokens.Select(t => t.EndsSentence));
    }
}

public class VocabularyLearnerTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "Lernen: neue Vorschläge, Bestätigungen zählen hoch, Sortierung nach Häufigkeit")]
    public void Learn()
    {
        var (state, changed) = VocabularyLearner.Learn(VocabularyState.Empty,
            "Wir deployen auf Cubernetes.", "Wir deployen auf Kubernetes.", T0);
        Assert.Equal(1, changed);
        (state, changed) = VocabularyLearner.Learn(state, "Export aus Weglap.", "Export aus weclapp.", T0.AddMinutes(1));
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "weclapp", "Kubernetes" }, state.Suggestions.Select(s => s.Term));   // gleich oft: neuester zuerst

        (state, changed) = VocabularyLearner.Learn(state, "Wir nutzen Cubernetes heute", "Wir nutzen Kubernetes heute", T0.AddMinutes(2));
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Kubernetes", "weclapp" }, state.Suggestions.Select(s => s.Term));
        Assert.Equal(2, state.Suggestions[0].Count);
        Assert.Equal(T0.AddMinutes(2), state.Suggestions[0].LastSeen);
        Assert.Equal(2, state.PendingCount);

        var (unchanged, none) = VocabularyLearner.Learn(state, "Das finde ich gut.", "Das finde ich hervorragend.", T0);
        Assert.Equal(0, none);
        Assert.Same(state, unchanged);
    }

    [Fact(DisplayName = "Bekannte und ignorierte Korrekturen werden nicht vorgeschlagen")]
    public void SkipsKnownAndIgnored()
    {
        var state = VocabularyState.Empty with
        {
            Entries = new[] { new VocabularyEntry("weclapp", new[] { "Weglap" }) },
            IgnoredKeys = new HashSet<string> { VocabularySuggestion.KeyFor("Kubernetes", "Cubernetes") },
        };
        Assert.Equal(0, VocabularyLearner.Learn(state, "Export aus Weglap.", "Export aus weclapp.").Changed);
        Assert.Equal(0, VocabularyLearner.Learn(state, "Wir deployen auf CUBERNETES.", "Wir deployen auf kubernetes.").Changed);
    }

    [Fact(DisplayName = "Übernehmen: als Variante eines vorhandenen Begriffs oder als neuer Eintrag")]
    public void Accept()
    {
        var existing = new VocabularyEntry("weclapp", new[] { "Weglap" });
        var state = VocabularyState.Empty with { Entries = new[] { existing } };
        var s1 = new VocabularySuggestion("WeClapp", "Weck lab", T0);
        var s2 = new VocabularySuggestion("Kubernetes", "Cubernetes", T0);
        var s3 = new VocabularySuggestion("weclapp", "weglap", T0);
        state = state with { Suggestions = new[] { s1, s2, s3 } };

        state = VocabularyLearner.Accept(state, s1);
        Assert.Equal(new[] { "Weglap", "Weck lab" }, state.Entries[0].Aliases);
        Assert.Equal(new[] { "Weglap" }, existing.Aliases);   // Eingabe unverändert
        state = VocabularyLearner.Accept(state, s3);          // Variante schon bekannt (ohne Groß-/Kleinschreibung)
        Assert.Equal(new[] { "Weglap", "Weck lab" }, state.Entries[0].Aliases);
        state = VocabularyLearner.Accept(state, s2);
        Assert.Equal("Kubernetes", state.Entries[1].Term);
        Assert.Equal(new[] { "Cubernetes" }, state.Entries[1].Aliases);
        Assert.Empty(state.Suggestions);
    }

    [Fact(DisplayName = "Ignorieren merkt den Schlüssel, Aktualisieren ersetzt per ID")]
    public void IgnoreAndUpdate()
    {
        var s = new VocabularySuggestion("Kubernetes", "Cubernetes", T0);
        var state = VocabularyState.Empty with { Suggestions = new[] { s } };
        var edited = VocabularyLearner.Update(state, s with { Term = "K8s" });
        Assert.Equal("K8s", edited.Suggestions[0].Term);
        Assert.Same(state, VocabularyLearner.Update(state, new VocabularySuggestion("x", "y")));

        var ignored = VocabularyLearner.Ignore(state, s);
        Assert.Empty(ignored.Suggestions);
        Assert.Contains("cubernetes→kubernetes", ignored.IgnoredKeys);
        Assert.Empty(state.IgnoredKeys);
    }

    [Fact(DisplayName = "Hinweistext für neue Vorschläge")]
    public void CountLabel()
    {
        Assert.Equal("1 neuer Wörterbuch-Vorschlag", VocabularySuggestion.CountLabel(1));
        Assert.Equal("3 neue Wörterbuch-Vorschläge", VocabularySuggestion.CountLabel(3));
    }
}
