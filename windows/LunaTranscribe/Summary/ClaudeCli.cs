using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Summary;

/// <summary>
/// <c>claude -p</c> als reine Textverarbeitung: ohne Werkzeuge, ohne MCP-Server, ohne Projekt- oder Nutzereinstellungen,
/// ohne gespeicherte Sitzung. Text per stdin, Antwort über stdout. Wird auch von <c>ClaudeRefiner</c> (Nachbearbeitung
/// mit Haiku) benutzt.
///
/// Läuft die CLI nur über cmd.exe (<see cref="CliProcess.ViaCmd"/>), geht die Anweisung als
/// <c>--system-prompt-file</c> hinein, weil cmd.exe keine Zeilenumbrüche in Argumenten übergibt.
/// </summary>
public sealed class ClaudeCli : ISummaryCli
{
    public CliProcess Process { get; }

    public ClaudeCli(CliProcess process) => Process = process;

    public static ClaudeCli? Locate(string? customPath) =>
        CliProcess.Locate(SummaryProvider.Claude, customPath) is { } p ? new ClaudeCli(p) : null;

    public Task<string> CompleteAsync(string instructions, string input, string? model, TimeSpan timeout, CancellationToken ct) =>
        CompleteAsync(instructions, input, model, effort: null, timeout, ct);

    /// <param name="effort">Aufwandsstufe der CLI (<c>low</c> … <c>max</c>); null = Standard.</param>
    public async Task<string> CompleteAsync(string instructions, string input, string? model, string? effort,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var output = await Process.RunAsync(workDir =>
        {
            var arguments = new List<string>
            {
                "-p",
                "--tools", "",
                "--strict-mcp-config",
                "--setting-sources", "",
                "--no-session-persistence",
                "--disable-slash-commands",
                "--output-format", "text",
            };
            if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
            if (effort != null) { arguments.Add("--effort"); arguments.Add(effort); }
            if (Process.ViaCmd)
            {
                var promptFile = Path.Combine(workDir, "system-prompt.txt");
                File.WriteAllText(promptFile, instructions, new UTF8Encoding(false));
                arguments.Add("--system-prompt-file");
                arguments.Add(promptFile);
            }
            else
            {
                arguments.Add("--system-prompt");
                arguments.Add(instructions);
            }
            return arguments;
        }, input, timeout ?? ISummaryCli.DefaultTimeout, ct);
        if (output.Length == 0) throw CliException.EmptyOutput(SummaryProvider.Claude);
        return output;
    }
}
