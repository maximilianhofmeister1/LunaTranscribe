using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LunaTranscribe.Summary;

public enum ClaudeCliErrorKind { NotFound, Failed, TimedOut, EmptyOutput }

/// <summary>Fehler der Claude CLI mit deutscher Meldung für die Oberfläche.</summary>
public sealed class ClaudeCliException : Exception
{
    public ClaudeCliErrorKind Kind { get; }

    private ClaudeCliException(ClaudeCliErrorKind kind, string message) : base(message) => Kind = kind;

    public static ClaudeCliException NotFound() => new(ClaudeCliErrorKind.NotFound,
        "Die Claude CLI wurde nicht gefunden. Installieren (PowerShell): irm https://claude.ai/install.ps1 | iex – " +
        "oder den Pfad in den Einstellungen → Meetings angeben.");

    public static ClaudeCliException Failed(int status, string message)
    {
        var detail = message.Trim();
        if (detail.Contains("login", StringComparison.OrdinalIgnoreCase) || detail.Contains("auth", StringComparison.OrdinalIgnoreCase))
            return new(ClaudeCliErrorKind.Failed,
                $"Die Claude CLI ist nicht angemeldet. Im Terminal einmal „claude“ starten und anmelden. ({Shorten(detail)})");
        return new(ClaudeCliErrorKind.Failed,
            $"Die Claude CLI ist mit Code {status} abgebrochen: {(detail.Length == 0 ? "keine Meldung" : Shorten(detail))}");
    }

    public static ClaudeCliException TimedOut(TimeSpan timeout) =>
        new(ClaudeCliErrorKind.TimedOut, $"Die Claude CLI hat nach {(int)timeout.TotalSeconds} s nicht geantwortet.");

    public static ClaudeCliException EmptyOutput() =>
        new(ClaudeCliErrorKind.EmptyOutput, "Die Claude CLI hat eine leere Antwort geliefert.");

    private static string Shorten(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Ruft die lokal installierte Claude CLI (<c>claude -p</c>) als reine Textverarbeitung auf: ohne Werkzeuge, ohne
/// MCP-Server, ohne Projekt- oder Nutzereinstellungen, ohne gespeicherte Sitzung. Der Text geht per stdin (UTF-8)
/// hinein, die Antwort kommt über stdout zurück.
///
/// Achtung Local-First: Die CLI läuft lokal, das Modell aber bei Anthropic. Der übergebene Text verlässt damit den
/// PC (über das Claude-Konto des Nutzers). Die Oberfläche weist darauf hin.
///
/// Windows: Der native Installer legt <c>claude.exe</c> ab; npm einen <c>claude.cmd</c>-Shim. Shims werden nach
/// Möglichkeit aufgelöst (node.exe + cli.js bzw. die eigentliche .exe), weil <c>cmd.exe</c> keine Zeilenumbrüche in
/// Argumenten durchreicht. Nur wenn das nicht geht, läuft der Shim über <c>cmd.exe /c</c> – dann mit
/// <c>--system-prompt-file</c> statt <c>--system-prompt</c>.
/// </summary>
public sealed class ClaudeCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Gefundener Pfad (für Anzeige und Log), z. B. <c>…\.local\bin\claude.exe</c> oder <c>…\npm\claude.cmd</c>.</summary>
    public string Executable { get; }

    /// <summary>Tatsächlich gestartetes Programm und vorangestellte Argumente (z. B. node.exe + cli.js).</summary>
    private readonly string _fileName;
    private readonly string[] _prefix;
    /// <summary>Nur über cmd.exe startbar (Shim nicht auflösbar).</summary>
    private readonly bool _viaCmd;

    private ClaudeCli(string executable, string fileName, string[] prefix, bool viaCmd)
    {
        Executable = executable;
        _fileName = fileName;
        _prefix = prefix;
        _viaCmd = viaCmd;
    }

    // MARK: Finden

    /// <summary>
    /// Sucht die CLI: eingestellter Pfad → übliche Installationsorte → <c>where claude</c>. Apps, die beim Login
    /// starten, sehen einen später geänderten PATH nicht, deshalb die festen Orte zuerst.
    /// </summary>
    public static ClaudeCli? Locate(string? customPath)
    {
        var custom = ExpandPath(customPath ?? "");
        if (custom.Length > 0)
        {
            foreach (var candidate in new[] { custom, custom + ".exe", custom + ".cmd" })
                if (File.Exists(candidate)) return FromPath(candidate);
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),            // nativer Installer
            Path.Combine(appData, "npm", "claude.cmd"),                    // npm install -g
            Path.Combine(localAppData, "Programs", "claude", "claude.exe"),
            Path.Combine(localAppData, "Programs", "claude-code", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.exe"),
            Path.Combine(home, ".claude", "local", "claude.cmd"),
        };
        foreach (var path in candidates)
            if (File.Exists(path)) return FromPath(path);

        return WhereLookup() is { } found ? FromPath(found) : null;
    }

    private static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }

    /// <summary><c>where claude</c> – findet, was im PATH dieses Prozesses liegt.</summary>
    private static string? WhereLookup()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "claude")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists).ToList();
            // .exe vor .cmd vor allem anderen (z. B. einer Unix-Shell-Datei ohne Endung).
            return lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault(l => l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ClaudeCli FromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            if (ResolveShim(path) is { } resolved) return new ClaudeCli(path, resolved.FileName, resolved.Prefix, false);
            return new ClaudeCli(path, path, Array.Empty<string>(), true);
        }
        if (ext == ".js") return NodePath(Path.GetDirectoryName(path)!) is { } node
            ? new ClaudeCli(path, node, new[] { path }, false)
            : new ClaudeCli(path, path, Array.Empty<string>(), true);
        return new ClaudeCli(path, path, Array.Empty<string>(), false);
    }

    /// <summary>
    /// npm-Shims rufen <c>"%dp0%\node_modules\…\cli.js"</c> (über node) oder eine .exe auf. Das Ziel lesen wir aus
    /// dem Shim, damit die Argumente ohne cmd.exe (und dessen Quoting) übergeben werden.
    /// </summary>
    private static (string FileName, string[] Prefix)? ResolveShim(string shim)
    {
        try
        {
            var dir = Path.GetDirectoryName(shim)!;
            var text = File.ReadAllText(shim);
            foreach (Match m in Regex.Matches(text, @"%~?dp0%?\\?([^""\r\n%]+\.(?:js|exe))", RegexOptions.IgnoreCase))
            {
                var target = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.TrimStart('\\')));
                if (!File.Exists(target)) continue;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetFileName(target).Equals("node.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    return (target, Array.Empty<string>());
                }
                if (NodePath(dir) is { } node) return (node, new[] { target });
            }
            // Bekannter npm-Aufbau, falls das Format des Shims abweicht.
            var cliJs = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(cliJs) && NodePath(dir) is { } n) return (n, new[] { cliJs });
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>node.exe neben dem Shim, sonst im PATH oder im Standard-Installationsort.</summary>
    private static string? NodePath(string shimDir)
    {
        var local = Path.Combine(shimDir, "node.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    // MARK: Aufrufen

    public Task<string> VersionAsync(CancellationToken ct = default) =>
        ExecuteAsync(new List<string> { "--version" }, input: null, systemPrompt: null, TimeSpan.FromSeconds(20), ct);

    /// <summary>Schickt <paramref name="input"/> mit der System-Anweisung <paramref name="instructions"/> an Claude und gibt die Antwort zurück.</summary>
    public async Task<string> CompleteAsync(string instructions, string input, string? model, TimeSpan? timeout = null, CancellationToken ct = default)
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
        var output = await ExecuteAsync(arguments, input, instructions, timeout ?? DefaultTimeout, ct);
        if (output.Length == 0) throw ClaudeCliException.EmptyOutput();
        return output;
    }

    /// <summary>
    /// Startet den Prozess in einem leeren Arbeitsordner (kein CLAUDE.md, kein Projektkontext) und liest stdout/stderr
    /// fortlaufend, damit große Antworten die Pipe nicht blockieren. Abbruch und Zeitlimit beenden den ganzen Prozessbaum.
    /// </summary>
    private async Task<string> ExecuteAsync(List<string> arguments, string? input, string? systemPrompt, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workDir = Path.Combine(Path.GetTempPath(), "LunaTranscribe-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var args = new List<string>(arguments);
            if (systemPrompt != null)
            {
                if (_viaCmd)
                {
                    // cmd.exe kann keine Zeilenumbrüche in Argumenten übergeben – Anweisung als Datei.
                    var promptFile = Path.Combine(workDir, "system-prompt.txt");
                    File.WriteAllText(promptFile, systemPrompt, new UTF8Encoding(false));
                    args.Add("--system-prompt-file");
                    args.Add(promptFile);
                }
                else
                {
                    args.Add("--system-prompt");
                    args.Add(systemPrompt);
                }
            }

            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (_viaCmd)
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                // /s: äußere Anführungszeichen entfernen, Rest unverändert ausführen.
                psi.Arguments = "/d /s /c \"" + string.Join(" ", new[] { _fileName }.Concat(args).Select(CmdQuote)) + "\"";
            }
            else
            {
                psi.FileName = _fileName;
                foreach (var a in _prefix) psi.ArgumentList.Add(a);
                foreach (var a in args) psi.ArgumentList.Add(a);
            }

            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw ClaudeCliException.NotFound(); }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // Eigener Task: Schreiben blockiert, bis die CLI liest.
            var writer = Task.Run(async () =>
            {
                try
                {
                    if (input != null) await process.StandardInput.WriteAsync(input);
                    process.StandardInput.Close();
                }
                catch (IOException) { /* Prozess hat stdin schon geschlossen */ }
            });

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw ClaudeCliException.TimedOut(timeout);
            }
            await writer;
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw ClaudeCliException.Failed(process.ExitCode, error.Trim().Length == 0 ? output : error);
            return output.Trim();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    /// <summary>Quoting für eine cmd.exe-Zeile: alles in Anführungszeichen, innere verdoppelt.</summary>
    private static string CmdQuote(string arg) => "\"" + arg.Replace("\"", "\"\"") + "\"";
}
