using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Audio;
using LunaTranscribe.Engine;
using LunaTranscribe.Output;

namespace LunaTranscribe.Meeting;

/// <summary>Fortschritt der Meeting-Verarbeitung: deutscher Schritt-Text und Anteil 0…1 über alles.</summary>
public sealed record MeetingProgress(string Step, double Fraction);

/// <summary>Ein Redebeitrag im fertigen Transkript (Sekunden ab Aufnahmestart). SpeakerId = „Sprecher 1“ …</summary>
public sealed record MeetingTurn(string SpeakerId, double Start, double End, string Text);

public enum MeetingTranscriberErrorKind { NoSpeech, AudioUnreadable }

public sealed class MeetingTranscriberException : Exception
{
    public MeetingTranscriberErrorKind Kind { get; }

    public MeetingTranscriberException(MeetingTranscriberErrorKind kind, Exception? inner = null)
        : base(kind == MeetingTranscriberErrorKind.NoSpeech
            ? "Im Meeting wurde keine Sprache erkannt."
            : "Die Aufnahmedatei konnte nicht gelesen werden.", inner) => Kind = kind;
}

/// <summary>
/// Verarbeitet eine Meeting-Aufnahme nach dem Meeting: Sprecher erkennen → Redebeiträge (Turns)
/// bilden → jeden Turn einzeln mit der normalen Diktier-Engine transkribieren.
///
/// „Turn-Slicing“ statt Wort-Alignment, weil es engine-unabhängig ist: Cohere liefert keine
/// Zeitstempel, Parakeet schon – so funktioniert beides über das unveränderte <see cref="ITranscriptionEngine"/>.
/// </summary>
public sealed class MeetingTranscriber
{
    /// <summary>Segmente desselben Sprechers mit kleinerer Lücke werden zu einem Turn zusammengefasst.</summary>
    public const double MaxGapWithinTurn = 1.0;
    /// <summary>Kürzere Turns sind für ASR unbrauchbar (halbe Wörter) und werden dem Vorgänger zugeschlagen.</summary>
    public const double MinTurnDuration = 0.5;
    /// <summary>Etwas Vorlauf/Nachlauf, damit Wortanfänge nicht abgeschnitten werden.</summary>
    public const double SlicePadding = 0.15;

    private readonly SpeakerDiarizer _diarizer;
    private readonly ITranscriptionEngine _engine;

    /// <param name="engine">Bereits geladene Engine; sie serialisiert Aufrufe selbst (Diktat darf parallel laufen).</param>
    public MeetingTranscriber(SpeakerDiarizer diarizer, ITranscriptionEngine engine)
    {
        _diarizer = diarizer;
        _engine = engine;
    }

    /// <summary>
    /// Transkribiert eine 16-kHz-Mono-Aufnahme. Das Sprechermodell wird bei Bedarf geladen und danach
    /// wieder entladen (RAM). Wirft <see cref="MeetingTranscriberException"/> oder <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="expectedSpeakers">Bekannte Teilnehmerzahl, 0 = automatisch erkennen.</param>
    /// <param name="progress">Wird von beliebigen Threads aufgerufen.</param>
    public async Task<IReadOnlyList<MeetingTurn>> RunAsync(string audioPath, AppLanguage language, int expectedSpeakers,
        Action<MeetingProgress> progress, CancellationToken ct)
    {
        // Fortschrittsbudget: 0–10 % Modelle, 10–40 % Sprecher, 40–100 % Transkription.
        var total = Stopwatch.StartNew();
        double audioSeconds;
        try { audioSeconds = WavFile.DurationSeconds(audioPath); }
        catch (Exception ex) { throw new MeetingTranscriberException(MeetingTranscriberErrorKind.AudioUnreadable, ex); }

        List<SpeakerSegment> turns;
        try
        {
            progress(new MeetingProgress("Sprechermodell wird geladen…", 0));
            await _diarizer.PrepareAsync(p => progress(new MeetingProgress("Sprechermodell wird geladen…", p * 0.1)), ct);
            ct.ThrowIfCancellationRequested();

            progress(new MeetingProgress("Sprecher werden erkannt…", 0.1));
            IReadOnlyList<SpeakerSegment> segments;
            try
            {
                segments = await _diarizer.DiarizeAsync(audioPath, expectedSpeakers,
                    p => progress(new MeetingProgress("Sprecher werden erkannt…", 0.1 + p * 0.3)), ct);
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or FormatException)
            {
                throw new MeetingTranscriberException(MeetingTranscriberErrorKind.AudioUnreadable, ex);
            }
            ct.ThrowIfCancellationRequested();

            turns = BuildTurns(segments, MaxGapWithinTurn, MinTurnDuration);
            Log.Write($"meeting: {segments.Count} segments → {turns.Count} turns ({audioSeconds:0.0}s audio, diarization done after {total.Elapsed.TotalSeconds:0.0}s)");
        }
        finally
        {
            // Sprechermodell nur während der Erkennung im RAM halten.
            _diarizer.Unload();
        }
        if (turns.Count == 0) throw new MeetingTranscriberException(MeetingTranscriberErrorKind.NoSpeech);

        var result = new List<MeetingTurn>();
        var asr = Stopwatch.StartNew();
        NAudio.Wave.WaveFileReader reader;
        try { reader = WavFile.Open16kMono(audioPath); }
        catch (Exception ex) { throw new MeetingTranscriberException(MeetingTranscriberErrorKind.AudioUnreadable, ex); }
        using (reader)
        {
            for (var index = 0; index < turns.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var turn = turns[index];
                progress(new MeetingProgress($"Transkribiere Beitrag {index + 1} von {turns.Count}…",
                    0.4 + 0.6 * index / turns.Count));

                var samples = WavFile.ReadSlice(reader, Math.Max(0, turn.Start - SlicePadding),
                    Math.Min(audioSeconds, turn.End + SlicePadding));
                if (samples.Length == 0) continue;
                var text = await _engine.TranscribeAsync(samples, language, ct);
                if (text.Length == 0) continue;
                result.Add(new MeetingTurn(turn.SpeakerId, turn.Start, turn.End, text));
            }
        }
        if (result.Count == 0) throw new MeetingTranscriberException(MeetingTranscriberErrorKind.NoSpeech);

        var renumbered = RenumberSpeakers(result);
        Log.Write($"meeting: {renumbered.Count} turns, {renumbered.Select(t => t.SpeakerId).Distinct().Count()} speakers, " +
                  $"transcription {asr.Elapsed.TotalSeconds:0.0}s, total {total.Elapsed.TotalSeconds:0.0}s");
        progress(new MeetingProgress("Fertig", 1));
        return renumbered;
    }

    // MARK: Turns

    /// <summary>Fasst aufeinanderfolgende Segmente desselben Sprechers zusammen und räumt Mini-Turns auf.</summary>
    public static List<SpeakerSegment> BuildTurns(IEnumerable<SpeakerSegment> segments, double maxGap, double minDuration)
    {
        var merged = new List<SpeakerSegment>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && merged[^1] is var last && last.SpeakerId == segment.SpeakerId && segment.Start - last.End <= maxGap)
                merged[^1] = last with { End = Math.Max(last.End, segment.End) };
            else
                merged.Add(segment);
        }

        // Zu kurze Turns: an den Vorgänger hängen (Zwischenrufe wie „ja“, „mhm“) oder verwerfen.
        var cleaned = new List<SpeakerSegment>();
        foreach (var turn in merged)
        {
            if (turn.End - turn.Start >= minDuration)
                cleaned.Add(turn);
            else if (cleaned.Count > 0 && cleaned[^1] is var last && turn.Start - last.End <= maxGap)
                cleaned[^1] = last with { End = Math.Max(last.End, turn.End) };
        }
        return cleaned;
    }

    /// <summary>Sprecher nach erstem Auftreten als „Sprecher 1“, „Sprecher 2“ … benennen.</summary>
    private static List<MeetingTurn> RenumberSpeakers(List<MeetingTurn> turns)
    {
        var mapping = new Dictionary<string, string>();
        return turns.Select(turn =>
        {
            if (!mapping.TryGetValue(turn.SpeakerId, out var name))
            {
                name = $"Sprecher {mapping.Count + 1}";
                mapping[turn.SpeakerId] = name;
            }
            return turn with { SpeakerId = name };
        }).ToList();
    }
}
