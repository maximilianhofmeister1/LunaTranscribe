using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LunaCore;

namespace LunaMCP.Server;

/// <summary>Kurzbeschreibung eines Meetings für Bestätigungsdialoge.</summary>
public sealed record MeetingDescription(Guid Id, string Title, DateTime Date, double DurationSeconds, IReadOnlyList<string> Speakers)
{
    public static MeetingDescription Of(MeetingTranscript meeting) =>
        new(meeting.Id, meeting.Title, meeting.Date, meeting.DurationSeconds, meeting.SpeakerIDs.Select(meeting.DisplayName).ToList());

    /// <summary>„„Planung Q4“ – 25.09.2026, 14:03 · 47 min · Anna, Sprecher 2“</summary>
    public string Line
    {
        get
        {
            var minutes = Math.Max(1, (int)Math.Round(DurationSeconds / 60, MidpointRounding.AwayFromZero));
            var people = Speakers.Count == 0 ? "" : " · " + string.Join(", ", Speakers);
            return $"„{Title}“ – {MeetingTranscript.Formatters.HeaderDate(Date)} · {minutes} min{people}";
        }
    }
}

public enum ConfirmationKind { Delete, RenameMeeting, RenameSpeaker }

/// <summary>Was der Nutzer bestätigen soll. Texte entstehen hier (testbar, deutsch); der Dialog zeigt sie nur an.</summary>
public sealed record ConfirmationRequest(ConfirmationKind Kind, IReadOnlyList<MeetingDescription> Meetings,
                                         string? NewTitle = null, string? Speaker = null, string? NewName = null)
{
    public static ConfirmationRequest Delete(IReadOnlyList<MeetingDescription> meetings) => new(ConfirmationKind.Delete, meetings);

    public static ConfirmationRequest RenameMeeting(MeetingDescription meeting, string newTitle) =>
        new(ConfirmationKind.RenameMeeting, new[] { meeting }, NewTitle: newTitle);

    public static ConfirmationRequest RenameSpeaker(MeetingDescription meeting, string speaker, string newName) =>
        new(ConfirmationKind.RenameSpeaker, new[] { meeting }, Speaker: speaker, NewName: newName);

    public string Title => Kind switch
    {
        ConfirmationKind.Delete => Meetings.Count == 1 ? "Meeting löschen?" : $"{Meetings.Count} Meetings löschen?",
        ConfirmationKind.RenameMeeting => "Meeting umbenennen?",
        _ => "Sprecher benennen?",
    };

    public string Message(string client)
    {
        var who = $"Claude ({client})";
        switch (Kind)
        {
            case ConfirmationKind.Delete:
                var list = string.Join("\n", Meetings.Select(m => "• " + m.Line));
                var subject = Meetings.Count == 1 ? "dieses Meeting" : "diese Meetings";
                return $"{who} möchte {subject} in den Papierkorb verschieben:\n\n{list}\n\nDas Transkript, die Zusammenfassung und eine behaltene Aufnahme wandern mit. Aus dem Papierkorb wiederherstellbar.";
            case ConfirmationKind.RenameMeeting:
                return $"{who} möchte das Meeting\n{Meetings[0].Line}\numbenennen in „{NewTitle}“.";
            default:
                var target = string.IsNullOrEmpty(NewName) ? "auf den ursprünglichen Namen zurücksetzen" : $"„{NewName}“ nennen";
                return $"{who} möchte in\n{Meetings[0].Line}\n„{Speaker}“ {target}.";
        }
    }

    public string ConfirmButton => Kind switch
    {
        ConfirmationKind.Delete => "In den Papierkorb",
        ConfirmationKind.RenameMeeting => "Umbenennen",
        _ => "Benennen",
    };

    public bool IsDestructive => Kind == ConfirmationKind.Delete;

    public bool Equals(ConfirmationRequest? other) =>
        other is not null && Kind == other.Kind && Meetings.SequenceEqual(other.Meetings)
        && NewTitle == other.NewTitle && Speaker == other.Speaker && NewName == other.NewName;

    public override int GetHashCode() => HashCode.Combine(Kind, Meetings.Count, NewTitle, Speaker, NewName);
}

public enum ConfirmationOutcomeKind { Approved, Declined, TimedOut, Unavailable }

/// <param name="Reason">Bei <see cref="ConfirmationOutcomeKind.Unavailable"/>: warum kein Dialog möglich war.</param>
public sealed record ConfirmationOutcome(ConfirmationOutcomeKind Kind, string? Reason = null)
{
    public static readonly ConfirmationOutcome Approved = new(ConfirmationOutcomeKind.Approved);
    public static readonly ConfirmationOutcome Declined = new(ConfirmationOutcomeKind.Declined);
    public static readonly ConfirmationOutcome TimedOut = new(ConfirmationOutcomeKind.TimedOut);
    public static ConfirmationOutcome Unavailable(string reason) => new(ConfirmationOutcomeKind.Unavailable, reason);
}

/// <summary>Fragt den Nutzer – außerhalb der Reichweite des Modells. luna-mcp zeigt einen nativen Windows-Dialog.</summary>
public interface IUserConfirmer
{
    Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, string client);
}

/// <summary>Ohne Dialog (Tests ohne Ersatz): lehnt ab. Im Zweifel wird nichts gelöscht.</summary>
public sealed class UnavailableConfirmer : IUserConfirmer
{
    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, string client) =>
        Task.FromResult(ConfirmationOutcome.Unavailable("Auf diesem System gibt es keinen Bestätigungsdialog."));
}

/// <summary>Bestätiger mit fester Antwort (Selbsttest).</summary>
public sealed class FixedConfirmer : IUserConfirmer
{
    private readonly ConfirmationOutcome _outcome;
    public FixedConfirmer(ConfirmationOutcome outcome) => _outcome = outcome;
    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, string client) => Task.FromResult(_outcome);
}

/// <summary>
/// Bremse gegen Dialog-Fluten (z. B. eine Prompt-Injection, die Löschen in Schleife auslöst):
/// immer nur ein offener Dialog, höchstens <c>maxDeleteDialogs</c> Lösch-Dialoge im <c>window</c>, und ein abgelehntes
/// Meeting wird für <c>window</c> nicht erneut angefragt.
/// </summary>
public sealed class ConfirmationGuard
{
    private readonly TimeSpan _window;
    private readonly int _maxDeleteDialogs;
    private readonly object _gate = new();
    private bool _dialogOpen;
    private readonly List<DateTime> _deleteDialogs = new();
    private readonly Dictionary<Guid, DateTime> _declined = new();

    public ConfirmationGuard(TimeSpan? window = null, int maxDeleteDialogs = 3)
    {
        _window = window ?? TimeSpan.FromMinutes(10);
        _maxDeleteDialogs = maxDeleteDialogs;
    }

    /// <exception cref="ToolError"/>
    public void Begin(IReadOnlyCollection<Guid>? deleting, DateTime now)
    {
        lock (_gate)
        {
            if (_dialogOpen)
                throw new ToolError("Es ist bereits ein Bestätigungsdialog offen. Warte auf die Antwort des Nutzers.");
            if (deleting is not null)
            {
                _deleteDialogs.RemoveAll(d => now - d > _window);
                foreach (var stale in _declined.Where(kv => now - kv.Value > _window).Select(kv => kv.Key).ToList())
                    _declined.Remove(stale);
                if (deleting.Any(_declined.ContainsKey))
                    throw new ToolError("Der Nutzer hat das Löschen dieses Meetings gerade abgelehnt. Nicht erneut anfragen, außer der Nutzer bittet ausdrücklich darum – dann in einigen Minuten.");
                if (_deleteDialogs.Count >= _maxDeleteDialogs)
                    throw new ToolError("Zu viele Lösch-Anfragen in kurzer Zeit. Bitte den Nutzer, Meetings in der App zu löschen, oder versuche es in einigen Minuten erneut.");
                _deleteDialogs.Add(now);
            }
            _dialogOpen = true;
        }
    }

    public void End(DateTime now, IEnumerable<Guid>? declinedIDs = null)
    {
        lock (_gate)
        {
            _dialogOpen = false;
            foreach (var id in declinedIDs ?? Enumerable.Empty<Guid>()) _declined[id] = now;
        }
    }
}
