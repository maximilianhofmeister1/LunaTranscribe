using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Summary;

/// <summary>
/// OpenAIs Codex CLI (<c>codex exec</c>) mit dem ChatGPT-Konto des Nutzers (auch Free). Die Anmeldung von
/// <c>codex login</c> liegt in <c>%USERPROFILE%\.codex</c>; <c>exec</c> nutzt sie ohne Rückfrage.
///
/// <c>codex exec</c> ist ein Coding-Agent. Damit er nur zusammenfasst: Sandbox nur lesend im leeren Arbeitsordner, keine
/// Freigaben, keine Websuche, keine Nutzer-Konfiguration (also auch keine MCP-Server aus <c>config.toml</c>), kein
/// AGENTS.md, keine gespeicherte Sitzung. Die Anweisung geht als <c>developer_instructions</c> hinein (einzeiliger
/// TOML-String, also auch über cmd.exe unkritisch), das Transkript per stdin als Kontext zum kurzen Prompt. Auf stdout
/// steht nur die letzte Antwort des Agenten, der Fortschritt geht nach stderr.
/// </summary>
public sealed class CodexCli : ISummaryCli
{
    public const string TaskPrompt = "Fasse das mitgelieferte Meeting-Transkript nach den Entwickler-Anweisungen zusammen. Antworte ausschließlich mit der Zusammenfassung.";

    public CliProcess Process { get; }

    public CodexCli(CliProcess process) => Process = process;

    public async Task<string> CompleteAsync(string instructions, string input, string? model, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureLoggedInAsync(ct);
        var output = await Process.RunAsync(_ =>
        {
            var arguments = new List<string>
            {
                "exec",
                "--ephemeral",
                "--skip-git-repo-check",
                "--ignore-user-config",
                "--sandbox", "read-only",
                "--color", "never",
                "-c", "approval_policy=\"never\"",
                "-c", "web_search=\"disabled\"",
                "-c", "project_doc_max_bytes=0",
                "-c", "developer_instructions=" + SummaryCliText.TomlString(instructions),
            };
            if (!string.IsNullOrWhiteSpace(model)) { arguments.Add("--model"); arguments.Add(model.Trim()); }
            arguments.Add(TaskPrompt);
            return arguments;
        }, input, timeout, ct);
        if (output.Length == 0) throw CliException.EmptyOutput(SummaryProvider.Codex);
        return output;
    }

    /// <summary>
    /// Ohne Anmeldung versucht <c>codex exec</c> mehrfach neu zu verbinden, bevor es mit „401 Unauthorized“ aufgibt (~15 s).
    /// <c>codex login status</c> sagt es in einer Sekunde und ohne Netz.
    /// </summary>
    private async Task EnsureLoggedInAsync(CancellationToken ct)
    {
        try
        {
            await Process.RunAsync(_ => new List<string> { "login", "status" }, input: null, TimeSpan.FromSeconds(20), ct);
        }
        catch (CliException ex) when (ex.Kind is CliErrorKind.Failed or CliErrorKind.TimedOut)
        {
            // Nur „nicht angemeldet“ zählt (geht als NotLoggedIn durch); alles andere klärt der eigentliche Aufruf.
        }
    }
}
