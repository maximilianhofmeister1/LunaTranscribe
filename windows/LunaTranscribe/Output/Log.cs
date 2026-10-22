using System;
using System.Diagnostics;
using System.IO;

namespace LunaTranscribe.Output;

/// <summary>
/// Minimales Diagnose-Log (Timing des Hotkey→Aufnahme→Einfügen-Pfads, Fehler).
/// Datei: %LOCALAPPDATA%\LunaTranscribe\luna.log – enthält nie Audio oder Transkripte.
/// </summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string Path =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LunaTranscribe", "luna.log");

    public static string FilePath => Path;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > 1_000_000) File.Delete(Path);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
