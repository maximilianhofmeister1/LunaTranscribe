using System;
using System.IO;
using LunaCore;

namespace LunaMCP;

/// <summary>
/// Diagnose-Log von luna-mcp: <c>%LOCALAPPDATA%\LunaTranscribe\luna-mcp.log</c> und stderr. <b>Nie stdout</b> –
/// im Serverbetrieb gehört stdout ausschließlich JSON-RPC. Enthält keine Transkript-Inhalte.
/// </summary>
public static class McpLog
{
    private static readonly object Gate = new();

    /// <summary>Zusätzlich auf stderr schreiben (Claude Code zeigt stderr in seinen MCP-Logs).</summary>
    public static bool Stderr { get; set; } = true;

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "luna-mcp.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}";
        lock (Gate)
        {
            if (Stderr)
            {
                try { Console.Error.WriteLine("luna-mcp: " + message); } catch (Exception) { }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, line + System.Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
