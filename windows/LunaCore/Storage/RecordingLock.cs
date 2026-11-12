using System;
using System.IO;

namespace LunaCore;

/// <summary>
/// Markiert eine laufende Aufnahme durch eine exklusiv geöffnete Sperrdatei im Auftragsordner (Gegenstück zu
/// <c>flock</c> der macOS-Version). Windows schließt das Handle, sobald der Prozess endet – auch bei Absturz oder
/// „Task beenden“. Damit lässt sich eindeutig unterscheiden: Sperre gehalten = Aufnahme läuft (in diesem oder einem
/// anderen Prozess, z. B. App + Selbsttest); Sperre frei = Aufnahme wurde unterbrochen.
/// </summary>
public sealed class RecordingLock : IDisposable
{
    public string Path { get; }
    private readonly object _gate = new();
    private FileStream? _stream;

    private RecordingLock(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    /// <summary>Übernimmt die Sperre (legt die Datei bei Bedarf an). null, wenn sie schon jemand hält.</summary>
    public static RecordingLock? Acquire(string path)
    {
        try
        {
            // FileShare.Delete: Release(removeFile) darf die Datei löschen, ohne die Sperre vorher aufzugeben.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
            return new RecordingLock(path, stream);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Hält gerade irgendein Prozess (auch dieser) die Sperre? Legt keine Datei an.</summary>
    public static bool IsHeld(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }   // Freigabeverletzung: jemand hält die Datei exklusiv offen
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Gibt die Sperre frei; <paramref name="removeFile"/> löscht die Sperrdatei (Aufnahme ist abgeschlossen).</summary>
    public void Release(bool removeFile = false)
    {
        lock (_gate)
        {
            if (_stream == null) return;
            if (removeFile) { try { File.Delete(Path); } catch (Exception) { /* bleibt liegen, stört nicht */ } }
            _stream.Dispose();
            _stream = null;
        }
    }

    public void Dispose() => Release();
}
