using System;
using System.IO;
using System.Threading;

namespace LunaCore;

/// <summary>
/// Atomares Schreiben: erst eine temporäre Datei im selben Ordner, dann Ersetzen per Umbenennen. Leser sehen
/// so nie eine halbe Datei. Kurze Sperren (Virenscanner, Cloud-Sync, ein Leser im anderen Prozess) werden
/// mit wenigen Wiederholungen abgefangen.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, data);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { /* Aufräumen ist best effort */ }
        }
    }
}
