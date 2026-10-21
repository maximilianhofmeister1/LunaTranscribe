using System;
using System.Collections.Generic;
using System.Linq;

namespace LunaCore;

/// <summary>Der Wert wurde nach Claudes Änderung erneut geändert – „Rückgängig“ würde fremde Arbeit überschreiben.</summary>
public sealed class UndoChangedSinceException : Exception
{
    public UndoChangedSinceException()
        : base("Der Wert wurde seitdem erneut geändert – bitte direkt im Meeting anpassen.") { }
}

/// <summary>
/// „Rückgängig“ für Umbenennungen durch Claude (Einstellungen → Datenschutz → „Zuletzt durch Claude“): wendet die
/// umgekehrte Umbenennung direkt über <see cref="MeetingLibrary"/> an – nur, wenn der Wert noch dem von Claude
/// gesetzten entspricht.
/// </summary>
public static class ActivityUndo
{
    public static bool CanUndo(ActivityEntry entry) =>
        entry.Action is ActivityAction.RenameMeeting or ActivityAction.RenameSpeaker
        && entry.OldValue is not null && entry.MeetingIDs.Count > 0;

    /// <summary>Macht die Änderung rückgängig und liefert den neuen Stand; sucht das Meeting in <paramref name="folders"/>.</summary>
    /// <exception cref="MeetingNotFoundException"/>
    /// <exception cref="UndoChangedSinceException"/>
    public static StoredMeeting Apply(ActivityEntry entry, IEnumerable<string> folders)
    {
        if (!CanUndo(entry)) throw new InvalidOperationException("Dieser Eintrag lässt sich nicht rückgängig machen.");
        var id = entry.MeetingIDs[0];
        var meeting = folders.SelectMany(f => MeetingLibrary.Scan(f)).FirstOrDefault(m => m.Id == id)
                      ?? throw new MeetingNotFoundException();
        var old = entry.OldValue!;

        switch (entry.Action)
        {
            case ActivityAction.RenameMeeting:
                if (meeting.Transcript.Title != entry.NewValue) throw new UndoChangedSinceException();
                return MeetingLibrary.Update(meeting.Folder, meeting.Revision, t => t.Title = old);
            default:
                if (entry.SpeakerID is not { } speaker || meeting.Transcript.DisplayName(speaker) != entry.NewValue)
                    throw new UndoChangedSinceException();
                return MeetingLibrary.Update(meeting.Folder, meeting.Revision, t =>
                {
                    if (old == speaker) t.SpeakerNames.Remove(speaker);
                    else t.SpeakerNames[speaker] = old;
                });
        }
    }
}
