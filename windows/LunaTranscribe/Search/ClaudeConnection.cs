using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace LunaTranscribe.Search;

/// <summary>
/// Einrichtung von luna-mcp in Claude: fertige Befehle zum Kopieren und ein Verbindungstest.
/// Die App schreibt bewusst nicht selbst in die Konfiguration anderer Programme.
/// </summary>
public static class ClaudeConnection
{
    /// <summary>luna-mcp liegt im Unterordner mcp\ neben LunaTranscribe.exe (eigene ONNX Runtime, siehe LunaMCP.csproj).</summary>
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "mcp", "luna-mcp.exe");

    public static bool HelperExists => File.Exists(HelperPath);

    /// <summary>Befehl für Claude Code (für alle Projekte des Nutzers); funktioniert in PowerShell und cmd.</summary>
    public static string ClaudeCodeCommand =>
        $"claude mcp add --scope user --transport stdio lunatranscribe -- \"{HelperPath}\"";

    /// <summary>Ort der Konfiguration von Claude Desktop.</summary>
    public static string ClaudeDesktopConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    /// <summary>Eintrag für <c>%APPDATA%\Claude\claude_desktop_config.json</c> (Backslashes JSON-maskiert).</summary>
    public static string ClaudeDesktopConfig =>
        "{\n" +
        "  \"mcpServers\": {\n" +
        "    \"lunatranscribe\": {\n" +
        $"      \"command\": {JsonSerializer.Serialize(HelperPath)}\n" +
        "    }\n" +
        "  }\n" +
        "}";

    /// <summary>In die Zwischenablage; die ist manchmal kurz von einer anderen App gesperrt – dann kurz erneut versuchen.</summary>
    public static bool Copy(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        return false;
    }

    /// <summary>Startet <c>luna-mcp --check</c> (nur lesend) und liefert dessen Ausgabe.</summary>
    public static Task<(bool Ok, string Output)> CheckAsync() => Task.Run(async () =>
    {
        if (!HelperExists) return (false, $"luna-mcp wurde nicht gefunden ({HelperPath}). Bitte LunaTranscribe neu installieren.");
        try
        {
            using var process = Process.Start(HelperStartInfo("--check"))!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return (false, "luna-mcp hat nicht innerhalb von 30 Sekunden geantwortet.");
            }
            var text = ((await output).Trim() + "\n" + (await error).Trim()).Trim();
            return process.ExitCode == 0 ? (true, text) : (false, "luna-mcp meldet einen Fehler: " + text);
        }
        catch (Exception ex)
        {
            return (false, "luna-mcp ließ sich nicht starten: " + ex.Message);
        }
    });

    /// <summary>Startinfo für luna-mcp als verstecktes Kindprogramm mit UTF-8-Ausgabe.</summary>
    internal static ProcessStartInfo HelperStartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(HelperPath)!,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }
}
