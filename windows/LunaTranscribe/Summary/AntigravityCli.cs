using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Summary;

/// <summary>
/// Googles Antigravity CLI (<c>agy</c>, Nachfolger der Gemini CLI) mit dem Google-Konto bzw. Gemini-Abo des Nutzers.
///
/// Anders als <c>claude</c> kennt <c>agy</c> (Doku Stand 11/2026) weder eine eigene System-Anweisung noch einen Schalter,
/// der alle Werkzeuge abschaltet. Deshalb gehen Anweisung und Transkript gemeinsam als Prompt hinein
/// (<see cref="SummaryCliText.AntigravityPrompt"/>), und der Agent läuft im leeren Arbeitsordner: Werkzeuge, die eine
/// Freigabe bräuchten, lehnt der Druckmodus ohne Terminal von selbst ab, und lesen kann er dort nichts.
///
/// Windows-Abweichung von macOS (dort <c>agy -p &lt;prompt&gt;</c>): Der Prompt geht als NDJSON-Nachricht über stdin
/// (<c>--input-format stream-json</c>), weil Windows die Befehlszeile auf 32 767 Zeichen begrenzt – ein einstündiges
/// Meeting passt nicht hinein – und <c>agy -p</c> stdin nicht liest. Nebenbei steht das Transkript so nicht in der
/// Befehlszeile des Prozesses, die andere Programme auslesen können. Die Antwort ist <c>response</c> im abschließenden
/// <c>result</c>-Ereignis (<c>--output-format stream-json</c>). Geprüft mit agy 1.5.10.
/// </summary>
public sealed class AntigravityCli : ISummaryCli
{
    public CliProcess Process { get; }

    public AntigravityCli(CliProcess process) => Process = process;

    public async Task<string> CompleteAsync(string instructions, string input, string? model, TimeSpan timeout, CancellationToken ct)
    {
        var output = await Process.RunAsync(_ =>
        {
            var arguments = new List<string>
            {
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--disable-slash-commands",
            };
            if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
            return arguments;
        }, SummaryCliText.AntigravityStreamInput(SummaryCliText.AntigravityPrompt(instructions, input)), timeout, ct);

        // agy beendet sich auch bei Fehlern mit Code 0 – maßgeblich ist das result-Ereignis.
        var result = SummaryCliText.ParseAntigravityResult(output)
                     ?? throw CliException.Failed(SummaryProvider.Antigravity, 0, SummaryCliText.Essence(output));
        if (!result.Success) throw CliException.Classify(SummaryProvider.Antigravity, 1, result.Error ?? "");
        return result.Response.Length > 0 ? result.Response : throw CliException.EmptyOutput(SummaryProvider.Antigravity);
    }
}
