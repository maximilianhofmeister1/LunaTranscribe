using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace LunaTranscribe.Output;

/// <summary>
/// Pausiert laufende Medienwiedergabe (Spotify, Musik, Browser-Video …) während der Aufnahme
/// und setzt sie danach fort – aber nur, wenn wir sie selbst pausiert haben.
///
/// Technik: Windows „System Media Transport Controls“ (die Wiedergabe-Steuerung, die auch beim
/// Drücken der Lautstärketasten erscheint). Jede App, die sich dort registriert, lässt sich so steuern.
/// </summary>
public sealed class MediaPlaybackController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly List<string> _pausedByUs = new();
    private readonly object _lock = new();

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> ManagerAsync()
    {
        if (_manager != null) return _manager;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Write("media: SMTC nicht verfügbar: " + ex.Message);
        }
        return _manager;
    }

    /// <summary>Initialisiert die WinRT-Sitzungsverwaltung vorab (erster Aufruf kann ~0,5 s dauern).</summary>
    public async Task WarmUpAsync()
    {
        var manager = await ManagerAsync();
        try { manager?.GetSessions(); } catch { }
    }

    /// <summary>Pausiert alle Sitzungen, die gerade spielen. Dauert typischerweise 10–50 ms.</summary>
    public async Task PauseIfPlayingAsync()
    {
        lock (_lock) _pausedByUs.Clear();
        var manager = await ManagerAsync();
        if (manager == null) return;
        try
        {
            foreach (var session in manager.GetSessions())
            {
                if (session.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                if (await session.TryPauseAsync())
                    lock (_lock) _pausedByUs.Add(session.SourceAppUserModelId);
            }
        }
        catch (Exception ex)
        {
            Log.Write("media pause failed: " + ex.Message);
        }
    }

    /// <summary>Setzt fort, was wir zuvor pausiert haben.</summary>
    public async Task ResumeIfPausedAsync()
    {
        List<string> ids;
        lock (_lock)
        {
            if (_pausedByUs.Count == 0) return;
            ids = new List<string>(_pausedByUs);
            _pausedByUs.Clear();
        }
        var manager = await ManagerAsync();
        if (manager == null) return;
        try
        {
            foreach (var session in manager.GetSessions())
            {
                if (!ids.Contains(session.SourceAppUserModelId)) continue;
                if (session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    await session.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Write("media resume failed: " + ex.Message);
        }
    }
}
