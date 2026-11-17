using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Summary;

public enum CliErrorKind { NotFound, NotLoggedIn, Failed, TimedOut, EmptyOutput }

/// <summary>
/// Fehler beim Aufruf einer der Anbieter-CLIs (Claude, Antigravity, Codex). Die Meldungen nennen das jeweilige Programm
/// und den nächsten Schritt, weil sie unverändert in der Oberfläche landen.
/// </summary>
public sealed class CliException : Exception
{
    public SummaryProvider Provider { get; }
    public CliErrorKind Kind { get; }

    /// <summary>Die CLI ist installiert, aber nicht angemeldet („Not logged in“, Auth-Fehler).</summary>
    public bool IsNotLoggedIn => Kind == CliErrorKind.NotLoggedIn;

    private CliException(SummaryProvider provider, CliErrorKind kind, string message) : base(message)
    {
        Provider = provider;
        Kind = kind;
    }

    public static CliException NotFound(SummaryProvider p) => new(p, CliErrorKind.NotFound,
        $"Die {p.CliName()} wurde nicht gefunden. Installieren: {p.InstallCommand()} – oder den Pfad in den Einstellungen → Meetings angeben.");

    public static CliException NotLoggedIn(SummaryProvider p, string detail) => new(p, CliErrorKind.NotLoggedIn,
        $"Die {p.CliName()} ist nicht angemeldet. Im Terminal einmal „{p.LoginCommand()}“ starten und anmelden. ({Shorten(detail, 200)})");

    public static CliException Failed(SummaryProvider p, int status, string message)
    {
        var detail = message.Trim();
        return new(p, CliErrorKind.Failed,
            $"Die {p.CliName()} ist mit Code {status} abgebrochen: {(detail.Length == 0 ? "keine Meldung" : Shorten(detail, 300))}");
    }

    public static CliException TimedOut(SummaryProvider p, TimeSpan timeout) =>
        new(p, CliErrorKind.TimedOut, $"Die {p.CliName()} hat nach {(int)timeout.TotalSeconds} s nicht geantwortet.");

    public static CliException EmptyOutput(SummaryProvider p) =>
        new(p, CliErrorKind.EmptyOutput, $"Die {p.CliName()} hat eine leere Antwort geliefert.");

    /// <summary>Anmeldefehler bekommen eine eigene Meldung mit dem Login-Befehl; alles andere geht durch.</summary>
    public static CliException Classify(SummaryProvider p, int status, string message)
    {
        var detail = Essence(message);
        string[] loginHints = { "not logged in", "log in", "login", "authentication required", "unauthorized", "auth" };
        return loginHints.Any(h => detail.Contains(h, StringComparison.OrdinalIgnoreCase))
            ? NotLoggedIn(p, detail)
            : Failed(p, status, detail);
    }

    /// <summary>Der aussagekräftige Teil einer Fehlerausgabe (siehe <see cref="SummaryCliText.Essence"/>).</summary>
    internal static string Essence(string message) => SummaryCliText.Essence(message);

    private static string Shorten(string s, int max) => s.Length > max ? s[..max] : s;
}

public static class SummaryProviderCliExtensions
{
    /// <summary>Offizieller Installationsweg unter Windows laut Anbieter-Doku.</summary>
    public static string InstallCommand(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "winget install Google.AntigravityCLI (oder PowerShell: irm https://antigravity.google/cli/install.ps1 | iex)",
        SummaryProvider.Codex => "npm install -g @openai/codex",
        _ => "PowerShell: irm https://claude.ai/install.ps1 | iex",
    };

    /// <summary>Befehl zum einmaligen Anmelden im Terminal.</summary>
    public static string LoginCommand(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "agy",
        SummaryProvider.Codex => "codex login",
        _ => "claude",
    };
}

/// <summary>
/// Startet eine lokal installierte CLI als reine Textverarbeitung: leerer, danach gelöschter Arbeitsordner (kein
/// CLAUDE.md, AGENTS.md oder GEMINI.md aus einem Projekt), Text per stdin (UTF-8), Antwort über stdout. stdout und stderr
/// werden fortlaufend gelesen, damit große Antworten die Pipe nicht blockieren.
///
/// Windows: Native Installer legen eine <c>.exe</c> ab, npm einen <c>.cmd</c>-Shim. Shims werden nach Möglichkeit
/// aufgelöst (node.exe + Skript bzw. die eigentliche .exe), weil <c>cmd.exe</c> keine Zeilenumbrüche in Argumenten
/// durchreicht und <c>%</c> expandiert. Nur wenn das nicht geht, läuft der Shim über <c>cmd.exe /c</c>
/// (<see cref="ViaCmd"/>) – Aufrufer übergeben lange oder mehrzeilige Texte dann als Datei oder per stdin.
/// </summary>
public sealed class CliProcess
{
    public SummaryProvider Provider { get; }

    /// <summary>ERROR_FILENAME_EXCED_RANGE: Befehlszeile länger als 32 767 Zeichen.</summary>
    private const int ErrorFilenameExceedsRange = 206;

    /// <summary>Gefundener Pfad (für Anzeige und Log), z. B. <c>…\.local\bin\claude.exe</c> oder <c>…\npm\codex.cmd</c>.</summary>
    public string Executable { get; }

    /// <summary>Nur über cmd.exe startbar (Shim nicht auflösbar).</summary>
    public bool ViaCmd { get; }

    /// <summary>Tatsächlich gestartetes Programm und vorangestellte Argumente (z. B. node.exe + codex.js).</summary>
    private readonly string _fileName;
    private readonly string[] _prefix;

    private CliProcess(SummaryProvider provider, string executable, string fileName, string[] prefix, bool viaCmd)
    {
        Provider = provider;
        Executable = executable;
        _fileName = fileName;
        _prefix = prefix;
        ViaCmd = viaCmd;
    }

    // MARK: Finden

    /// <summary>
    /// Sucht die CLI: eingestellter Pfad → übliche Installationsorte → PATH (der aktuelle aus der Registry, nicht nur der
    /// beim Start geerbte: Eine App aus dem Autostart sieht eine danach installierte CLI sonst erst nach der Neuanmeldung).
    /// </summary>
    public static CliProcess? Locate(SummaryProvider provider, string? customPath)
    {
        var custom = ExpandPath(customPath ?? "");
        if (custom.Length > 0)
        {
            foreach (var candidate in new[] { custom, custom + ".exe", custom + ".cmd" })
                if (File.Exists(candidate)) return FromPath(provider, candidate);
            return null;
        }

        foreach (var path in Candidates(provider))
            if (File.Exists(path)) return FromPath(provider, path);

        return PathLookup(provider.Command()) is { } found ? FromPath(provider, found) : null;
    }

    private static IEnumerable<string> Candidates(SummaryProvider provider)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var command = provider.Command();
        var paths = new List<string> { Path.Combine(home, ".local", "bin", command + ".exe") };   // native Installer
        switch (provider)
        {
            case SummaryProvider.Claude:
                paths.Add(Path.Combine(appData, "npm", "claude.cmd"));                         // npm install -g
                paths.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
                paths.Add(Path.Combine(localAppData, "Programs", "claude-code", "claude.exe"));
                paths.Add(Path.Combine(home, ".claude", "local", "claude.exe"));
                paths.Add(Path.Combine(home, ".claude", "local", "claude.cmd"));
                break;
            case SummaryProvider.Antigravity:
                paths.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "agy.exe")); // winget (portable, Link)
                paths.AddRange(WingetPackageBinaries("Google.AntigravityCLI_", "agy.exe"));    // winget (portable, ohne Link)
                paths.Add(Path.Combine(localAppData, "Antigravity", "agy.exe"));                  // install.ps1
                paths.Add(Path.Combine(localAppData, "agy", "bin", "agy.exe"));
                paths.Add(Path.Combine(localAppData, "Programs", "Antigravity CLI", "agy.exe"));
                paths.Add(Path.Combine(home, ".antigravity", "bin", "agy.exe"));
                paths.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin", "agy.exe"));
                break;
            case SummaryProvider.Codex:
                paths.Add(Path.Combine(appData, "npm", "codex.cmd"));                          // npm install -g
                paths.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "codex.exe"));
                paths.Add(Path.Combine(home, ".bun", "bin", "codex.exe"));
                paths.Add(Path.Combine(home, "scoop", "shims", "codex.exe"));
                paths.AddRange(NvmBinaries("codex.cmd"));
                break;
        }
        return paths;
    }

    /// <summary>winget legt portable Pakete unter <c>%LOCALAPPDATA%\Microsoft\WinGet\Packages\&lt;Id&gt;_&lt;Quelle&gt;</c> ab.</summary>
    private static IEnumerable<string> WingetPackageBinaries(string idPrefix, string file)
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        string[] dirs;
        try { dirs = Directory.Exists(packages) ? Directory.GetDirectories(packages, idPrefix + "*") : Array.Empty<string>(); }
        catch (Exception) { yield break; }
        foreach (var dir in dirs) yield return Path.Combine(dir, file);
    }

    /// <summary>
    /// nvm-windows legt globale npm-Pakete je Node-Version in <c>%NVM_HOME%\v…</c> ab und verlinkt nur die aktive nach
    /// <c>%NVM_SYMLINK%</c>. Die aktive Version zuerst, dann die neueste.
    /// </summary>
    private static IEnumerable<string> NvmBinaries(string file)
    {
        var symlink = Environment.GetEnvironmentVariable("NVM_SYMLINK", EnvironmentVariableTarget.User)
                      ?? Environment.GetEnvironmentVariable("NVM_SYMLINK", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrEmpty(symlink)) yield return Path.Combine(symlink, file);
        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.User)
                      ?? Environment.GetEnvironmentVariable("NVM_HOME", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrEmpty(nvmHome) || !Directory.Exists(nvmHome)) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(nvmHome, "v*"); }
        catch (Exception) { yield break; }
        foreach (var dir in dirs.OrderByDescending(d => Version.TryParse(Path.GetFileName(d).TrimStart('v'), out var v) ? v : new Version()))
            yield return Path.Combine(dir, file);
    }

    private static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return path;
    }

    /// <summary>
    /// PATH dieses Prozesses plus der aktuelle Nutzer- und System-PATH aus der Registry (ohne Doppelte, Variablen
    /// expandiert).
    /// </summary>
    internal static List<string> SearchPath()
    {
        var parts = new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            }
            .SelectMany(p => (p ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim('"')))
            .Where(p => p.Length > 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return parts.Where(seen.Add).ToList();
    }

    /// <summary>Sucht <paramref name="command"/> im PATH – <c>.exe</c> vor <c>.cmd</c>/<c>.bat</c> (wie die Shell).</summary>
    private static string? PathLookup(string command)
    {
        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
            foreach (var dir in SearchPath())
            {
                try
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        return null;
    }

    private static CliProcess FromPath(SummaryProvider provider, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            if (ResolveShim(provider, path) is { } resolved) return new CliProcess(provider, path, resolved.FileName, resolved.Prefix, false);
            return new CliProcess(provider, path, path, Array.Empty<string>(), true);
        }
        if (ext is ".js" or ".mjs") return NodePath(Path.GetDirectoryName(path)!) is { } node
            ? new CliProcess(provider, path, node, new[] { path }, false)
            : new CliProcess(provider, path, path, Array.Empty<string>(), true);
        return new CliProcess(provider, path, path, Array.Empty<string>(), false);
    }

    /// <summary>
    /// npm-Shims rufen <c>"%dp0%\node_modules\…\cli.js"</c> (über node) oder eine .exe auf. Das Ziel lesen wir aus dem
    /// Shim, damit die Argumente ohne cmd.exe (und dessen Quoting) übergeben werden.
    /// </summary>
    private static (string FileName, string[] Prefix)? ResolveShim(SummaryProvider provider, string shim)
    {
        try
        {
            var dir = Path.GetDirectoryName(shim)!;
            var text = File.ReadAllText(shim);
            foreach (Match m in Regex.Matches(text, @"%~?dp0%?\\?([^""\r\n%]+\.(?:js|mjs|cjs|exe))", RegexOptions.IgnoreCase))
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
            var known = provider switch
            {
                SummaryProvider.Claude => Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js"),
                SummaryProvider.Codex => Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js"),
                _ => null,
            };
            if (known != null && File.Exists(known) && NodePath(dir) is { } n) return (n, new[] { known });
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>node.exe neben dem Shim, sonst im PATH oder im Standard-Installationsort.</summary>
    private static string? NodePath(string shimDir)
    {
        var local = Path.Combine(shimDir, "node.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in SearchPath())
        {
            try
            {
                var candidate = Path.Combine(dir, "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    // MARK: Aufrufen

    public Task<string> VersionAsync(CancellationToken ct = default) =>
        RunAsync(_ => new List<string> { "--version" }, input: null, TimeSpan.FromSeconds(20), ct);

    /// <summary>
    /// Startet den Prozess in einem leeren, danach gelöschten Arbeitsordner und gibt stdout zurück. Abbruch und Zeitlimit
    /// beenden den ganzen Prozessbaum.
    /// </summary>
    /// <param name="arguments">Baut die Argumente; bekommt den Arbeitsordner, falls Text als Datei übergeben werden muss.</param>
    /// <param name="input">Text für stdin; null = stdin bleibt leer.</param>
    public async Task<string> RunAsync(Func<string, List<string>> arguments, string? input, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workDir = Path.Combine(Path.GetTempPath(), $"LunaTranscribe-{Provider.Command()}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var args = arguments(workDir);
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
            ConfigureEnvironment(psi);
            if (ViaCmd)
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
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorFilenameExceedsRange)
            {
                // Windows begrenzt die Befehlszeile auf 32 767 Zeichen – Aufrufer übergeben langen Text per stdin.
                throw CliException.Failed(Provider, ex.NativeErrorCode, "Der Text ist zu lang für die Befehlszeile.");
            }
            catch (System.ComponentModel.Win32Exception) { throw CliException.NotFound(Provider); }

            // Fragt die CLI mitten im Lauf nach einer Anmeldung (agy wartet dann bis zu 60 s auf einen Code), sofort abbrechen.
            using var loginCts = new CancellationTokenSource();
            string? loginPrompt = null;
            void OnLoginPrompt(string text)
            {
                loginPrompt ??= text;
                try { loginCts.Cancel(); } catch (ObjectDisposedException) { }
            }
            var stdout = ReadAllAsync(process.StandardOutput, LoginPromptMarkers, OnLoginPrompt);
            var stderr = ReadAllAsync(process.StandardError, LoginErrorMarkers, OnLoginPrompt);
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token, loginCts.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                if (loginPrompt != null) throw CliException.NotLoggedIn(Provider, loginPrompt);
                throw CliException.TimedOut(Provider, timeout);
            }
            await writer;
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw CliException.Classify(Provider, process.ExitCode, error.Trim().Length == 0 ? output : error);
            return output.Trim();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Der Ordner der CLI und der aktuelle PATH aus der Registry zuerst: Eine per npm installierte Codex CLI ist ein
    /// Node-Skript und startet ihre eigentliche .exe; ohne Farben, damit keine Steuerzeichen in der Antwort landen.
    /// </summary>
    private void ConfigureEnvironment(ProcessStartInfo psi)
    {
        var dirs = new List<string> { Path.GetDirectoryName(Executable)! };
        if (_prefix.Length > 0 || !_fileName.Equals(Executable, StringComparison.OrdinalIgnoreCase))
            dirs.Add(Path.GetDirectoryName(_fileName)!);
        dirs.AddRange(SearchPath());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        psi.Environment["PATH"] = string.Join(';', dirs.Where(d => d.Length > 0 && seen.Add(d)));
        psi.Environment["NO_COLOR"] = "1";
    }

    /// <summary>Texte, mit denen eine CLI im Druckmodus interaktiv nach einer Anmeldung fragt.</summary>
    private static readonly string[] LoginPromptMarkers =
    {
        "Authentication required. Please visit the URL to log in",
        "paste the authorization code",
    };

    /// <summary>
    /// Zusätzlich auf stderr: Ohne Anmeldung versucht <c>codex exec</c> es zehnmal (~25 s) mit „401 Unauthorized“, bevor
    /// es aufgibt. Nur stderr, damit eine Zusammenfassung, die den Text zufällig enthält, nichts auslöst.
    /// </summary>
    private static readonly string[] LoginErrorMarkers = LoginPromptMarkers.Append("401 Unauthorized").ToArray();

    /// <summary>
    /// Liest einen Ausgabestrom vollständig und meldet eine Anmeldeaufforderung am Anfang der Ausgabe (nur die ersten
    /// 8 KB werden geprüft – sie kommt vor jeder Antwort).
    /// </summary>
    private static async Task<string> ReadAllAsync(StreamReader reader, string[] markers, Action<string> onLoginPrompt)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        var reported = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (reported || sb.Length > 8192) continue;
            var text = sb.ToString();
            if (markers.FirstOrDefault(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)) is { } marker)
            {
                reported = true;
                onLoginPrompt(text.Split('\n').First(l => l.Contains(marker, StringComparison.OrdinalIgnoreCase)).Trim());
            }
        }
        return sb.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }

    /// <summary>Quoting für eine cmd.exe-Zeile: alles in Anführungszeichen, innere verdoppelt.</summary>
    private static string CmdQuote(string arg) => "\"" + arg.Replace("\"", "\"\"") + "\"";
}
