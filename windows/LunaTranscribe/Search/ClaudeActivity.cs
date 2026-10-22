using System;

namespace LunaTranscribe.Search;

/// <summary>
/// Meldet Änderungen an Meetings, die nicht über die Meeting-Bibliothek der App liefen (z. B. „Rückgängig“ in
/// Einstellungen → Datenschutz). Die Meeting-Ansicht abonniert das und lädt das betroffene Meeting neu.
/// </summary>
public static class ClaudeActivity
{
    /// <summary>Wird auf dem UI-Thread ausgelöst; Argument ist die Meeting-ID.</summary>
    public static event Action<Guid>? MeetingChanged;

    public static void NotifyMeetingChanged(Guid id) => MeetingChanged?.Invoke(id);
}
