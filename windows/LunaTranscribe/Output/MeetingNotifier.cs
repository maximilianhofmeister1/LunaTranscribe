using System;

namespace LunaTranscribe.Output;

/// <summary>
/// Mitteilungen für Meetings, die im Hintergrund fertig (oder nicht fertig) wurden – statt ein Fenster zu öffnen,
/// das mitten in der Arbeit oder im nächsten Meeting stört. Ein Klick auf „fertig“ öffnet das Meeting.
/// Windows: Sprechblase des Infobereich-Symbols (erscheint unter Windows 10/11 als Benachrichtigung);
/// <see cref="Presenter"/> setzt die App auf <c>TrayIcon.ShowNotification</c>. Ohne Presenter (Selbsttests) nur Log.
/// </summary>
public sealed class MeetingNotifier
{
    /// <summary>Zeigt eine Mitteilung: Titel, Text, Aktion beim Klick (oder null).</summary>
    public Action<string, string, Action?>? Presenter { get; set; }

    /// <summary>Wird mit der Meeting-ID aufgerufen, wenn der Nutzer auf eine Fertig-Mitteilung klickt.</summary>
    public Action<Guid>? OnOpenMeeting { get; set; }

    /// <summary>Klick auf die Mitteilung über gerettete Aufnahmen: Meetings-Seite öffnen.</summary>
    public Action? OnOpenMeetings { get; set; }

    public void MeetingFinished(string title, Guid meetingId) =>
        Post("Meeting fertig transkribiert", $"„{title}“ liegt in deiner Bibliothek. Klicken zum Öffnen.",
            () => OnOpenMeeting?.Invoke(meetingId));

    public void MeetingFailed(string title, string message) =>
        Post("Meeting konnte nicht verarbeitet werden",
            $"„{title}“: {message} Die Aufnahme bleibt erhalten – im Menü erneut versuchen.", null);

    /// <summary>Beim Start wurden unterbrochene Aufnahmen gerettet.</summary>
    public void RecordingsRecovered(int count) =>
        Post(count == 1 ? "Unterbrochene Meeting-Aufnahme gerettet" : "Unterbrochene Meeting-Aufnahmen gerettet",
            count == 1
                ? "Ein Meeting lief noch, als LunaTranscribe beendet wurde. Die Aufnahme ist gesichert – klicken, um sie zu transkribieren oder zu verwerfen."
                : $"{count} Meetings liefen noch, als LunaTranscribe beendet wurde. Die Aufnahmen sind gesichert – klicken, um sie zu transkribieren oder zu verwerfen.",
            () => OnOpenMeetings?.Invoke());

    private void Post(string title, string body, Action? onClick)
    {
        Log.Write("notification: " + title);
        try { Presenter?.Invoke(title, body, onClick); }
        catch (Exception ex) { Log.Write("notification failed: " + ex.Message); }
    }
}
