using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Engine;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Die echte Meeting-Verarbeitung: <see cref="SpeakerDiarizer"/> erkennt die Sprecher, <see cref="MeetingTranscriber"/>
/// transkribiert jeden Redebeitrag einzeln. Der Diarizer lebt so lange wie die Warteschlange (Modelle lädt er pro Lauf
/// und gibt sie danach wieder frei).
/// </summary>
public sealed class TranscriberPipeline : IMeetingPipeline, IDisposable
{
    private readonly SpeakerDiarizer _diarizer = new();

    public async Task<IReadOnlyList<MeetingTurnResult>> RunAsync(string wavPath, AppLanguage language, int expectedSpeakers,
        ITranscriptionEngine engine, IProgress<(string Step, double Fraction)> progress, CancellationToken ct)
    {
        var transcriber = new MeetingTranscriber(_diarizer, engine);
        var turns = await transcriber.RunAsync(wavPath, language, expectedSpeakers,
            p => progress.Report((p.Step, p.Fraction)), ct);
        return turns.Select(t => new MeetingTurnResult(t.SpeakerId, t.Start, t.End, t.Text)).ToList();
    }

    public void Dispose() => _diarizer.Dispose();
}
