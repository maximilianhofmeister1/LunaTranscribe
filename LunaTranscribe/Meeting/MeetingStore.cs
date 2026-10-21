using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Meeting;

/// <summary>Kopfdaten eines Meetings für Listen – ohne die (großen) Turns.</summary>
/// <param name="IsUnsaved">Liegt im Rettungsordner, weil der Meeting-Ordner nicht erreichbar war.</param>
public sealed record MeetingEntry(Guid Id, string Title, DateTime Date, double DurationSeconds, int SpeakerCount,
                                  bool HasSummary, bool IsConfidential, string Folder, bool IsUnsaved);

/// <summary>Rückgängig ist nicht möglich, weil der Wert seitdem erneut geändert wurde.</summary>
public sealed class MeetingUndoException : Exception
{
    public MeetingUndoException() : base("Der Wert wurde seitdem erneut geändert – bitte direkt im Meeting anpassen.") { }
}

/// <summary>
/// Die Meeting-Bibliothek: ein Unterordner pro Meeting im frei wählbaren Meeting-Ordner (Aufbau siehe
/// <see cref="MeetingLibrary"/>). Lesen und Schreiben laufen über LunaCore – dieselben Funktionen nutzt luna-mcp.
///
/// Invariante: <b>Ein Transkript geht nie verloren.</b> Ist der Meeting-Ordner nicht erreichbar, landet ein neues
/// Meeting im Rettungsordner (<see cref="AppPaths.UnsavedMeetings"/>) und zieht beim nächsten erfolgreichen Laden
/// automatisch in die Bibliothek um.
///
/// Änderungen von außen (Claude über luna-mcp: umbenennen, Sprecher benennen, löschen) meldet das
/// Aktivitätsprotokoll; der Store lädt dann neu und meldet <see cref="ExternalChanges"/>. Läuft auf dem UI-Thread.
/// </summary>
public sealed class MeetingStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Meetings, die Claude über luna-mcp geändert oder gelöscht hat (nach dem Neuladen gemeldet).</summary>
    public event Action<IReadOnlySet<Guid>>? ExternalChanges;

    private readonly SettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    /// <summary>Volltext je Meeting (klein geschrieben) für die Suche; wird bei Bedarf gefüllt.</summary>
    private readonly Dictionary<Guid, string> _searchIndex = new();
    private readonly ActivityLog _activityLog = new();
    private readonly HashSet<MeetingDocument> _openDocuments = new();
    private DispatcherTimer? _watchTimer;
    /// <summary>Zuletzt gesehene Änderung im Aktivitätsprotokoll.</summary>
    private DateTime _lastActivity = DateTime.UtcNow;
    private (DateTime Write, long Length) _lastLogStamp;

    private IReadOnlyList<MeetingEntry> _entries = Array.Empty<MeetingEntry>();
    private string? _folderIssue;
    private readonly string? _libraryOverride;
    private readonly string? _unsavedOverride;

    /// <param name="libraryFolder">Nur Selbsttests: fester Meeting-Ordner statt der Einstellung.</param>
    /// <param name="unsavedFolder">Nur Selbsttests: Rettungsordner statt <see cref="AppPaths.UnsavedMeetings"/>.</param>
    public MeetingStore(SettingsStore settings, Dispatcher dispatcher, string? libraryFolder = null, string? unsavedFolder = null)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _libraryOverride = libraryFolder;
        _unsavedOverride = unsavedFolder;
        _lastLogStamp = LogStamp();
        Reload();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsStore.MeetingFolder) && _libraryOverride == null) Reload();
        };
    }

    /// <summary>Neueste zuerst.</summary>
    public IReadOnlyList<MeetingEntry> Entries
    {
        get => _entries;
        private set { _entries = value; OnPropertyChanged(); }
    }

    /// <summary>Gesetzt, wenn der Meeting-Ordner nicht angelegt oder gelesen werden kann.</summary>
    public string? FolderIssue
    {
        get => _folderIssue;
        private set { if (_folderIssue == value) return; _folderIssue = value; OnPropertyChanged(); }
    }

    public string LibraryFolder => _libraryOverride ?? AppPaths.MeetingFolder(_settings.MeetingFolder);

    /// <summary>Rettungsordner, falls der Meeting-Ordner nicht erreichbar ist.</summary>
    public string UnsavedFolder => _unsavedOverride ?? AppPaths.UnsavedMeetings;

    /// <summary>Startet die Beobachtung des Aktivitätsprotokolls von luna-mcp (nicht in Selbsttests).</summary>
    public void StartWatching()
    {
        if (_watchTimer != null) return;
        // Alle zwei Sekunden reicht: Claude ändert selten, und Änderungszeit und Größe einer Datei kosten praktisch nichts.
        _watchTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _watchTimer.Tick += (_, _) =>
        {
            var stamp = LogStamp();
            if (stamp == _lastLogStamp) return;
            _lastLogStamp = stamp;
            ApplyExternalChanges();
        };
        _watchTimer.Start();
    }

    public void StopWatching() => _watchTimer?.Stop();

    // MARK: Lesen

    /// <summary>Liest die Bibliothek neu ein und rettet liegengebliebene Meetings in die Bibliothek.</summary>
    public void Reload()
    {
        string? issue = null;
        try
        {
            Directory.CreateDirectory(LibraryFolder);
            MoveUnsavedIntoLibrary();
        }
        catch (Exception ex)
        {
            issue = $"Der Meeting-Ordner ist nicht erreichbar ({LibraryFolder}). Neue Meetings werden vorübergehend in der App gesichert.";
            Log.Write("meeting library folder unavailable: " + ex.Message);
        }
        FolderIssue = issue;
        var library = issue == null ? ReadEntries(LibraryFolder, unsaved: false) : new List<MeetingEntry>();
        var unsaved = ReadEntries(UnsavedFolder, unsaved: true);
        var all = library.Concat(unsaved).OrderByDescending(e => JsonCoding.ToUtc(e.Date)).ToList();
        var ids = all.Select(e => e.Id).ToHashSet();
        foreach (var stale in _searchIndex.Keys.Where(id => !ids.Contains(id)).ToList()) _searchIndex.Remove(stale);
        Entries = all;
    }

    public MeetingEntry? Entry(Guid id) => _entries.FirstOrDefault(e => e.Id == id);

    /// <exception cref="MeetingNotFoundException"/>
    public MeetingTranscript Load(Guid id) => MeetingLibrary.ReadTranscript(CurrentFolder(id));

    /// <summary>Meetings, deren Titel, Sprecher, Zusammenfassung oder Text <paramref name="query"/> enthalten.</summary>
    public IReadOnlyList<MeetingEntry> Search(string? query)
    {
        var needle = (query ?? "").Trim().ToLowerInvariant();
        if (needle.Length == 0) return _entries;
        return _entries.Where(e => e.Title.ToLowerInvariant().Contains(needle) || FullText(e).Contains(needle)).ToList();
    }

    /// <summary>Alle Meetings mit Inhalt (für einen Suchindex). Liest von der Platte – im Hintergrund aufrufen.</summary>
    public static List<StoredMeeting> StoredMeetings(string libraryFolder) =>
        MeetingLibrary.Scan(libraryFolder).Concat(MeetingLibrary.Scan(AppPaths.UnsavedMeetings, unsaved: true)).ToList();

    // MARK: Schreiben

    /// <summary>Legt ein neu verarbeitetes Meeting an. <paramref name="audioPath"/> wird in den Meeting-Ordner verschoben.</summary>
    /// <returns>Der neue Eintrag; null, wenn auch der Rettungsordner nicht beschreibbar war.</returns>
    public MeetingEntry? Add(MeetingTranscript transcript, string? audioPath = null)
    {
        var target = FolderIssue == null ? LibraryFolder : UnsavedFolder;
        try
        {
            var folder = MeetingLibrary.Write(transcript, target, existing: null);
            if (audioPath != null)
            {
                try { File.Move(audioPath, Path.Combine(folder, MeetingLibrary.AudioFileName)); }
                catch (Exception ex) { Log.Write("meeting audio not moved: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            // Letzte Rettung: Rettungsordner. Scheitert auch das, bleibt nur der Fehler im Log.
            Log.Write("saving meeting failed: " + ex.Message);
            if (target != UnsavedFolder)
            {
                try { MeetingLibrary.Write(transcript, UnsavedFolder, existing: null); }
                catch (Exception inner) { Log.Write("saving meeting to unsaved folder failed: " + inner.Message); }
            }
        }
        Reload();
        return Entry(transcript.Id);
    }

    /// <summary>Speichert Änderungen (Titel, Sprecher, Text, Zusammenfassung); benennt den Ordner bei neuem Titel um.</summary>
    public void Update(MeetingTranscript transcript)
    {
        var current = CurrentFolder(transcript.Id);
        var isUnsaved = Entry(transcript.Id)?.IsUnsaved ?? false;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current))!;
        var folder = MeetingLibrary.Write(transcript, parent, current);
        _searchIndex[transcript.Id] = SearchText(transcript);
        var list = _entries.ToList();
        var index = list.FindIndex(e => e.Id == transcript.Id);
        if (index >= 0)
        {
            list[index] = EntryFor(transcript, folder, isUnsaved);
            Entries = list;
        }
    }

    /// <summary>
    /// Speichert Änderungen aus einem geöffneten Meeting. Hat luna-mcp die Datei seit <paramref name="base"/> (dem
    /// zuletzt geladenen oder gespeicherten Stand) geändert, werden beide Seiten zusammengeführt: Was der Nutzer
    /// geändert hat, gewinnt, alles andere kommt von der Platte. Gibt den gespeicherten Stand zurück.
    /// </summary>
    public MeetingTranscript Save(MeetingTranscript local, MeetingTranscript @base)
    {
        var disk = Load(local.Id);
        var merged = disk.Equals(@base) ? local : MeetingLibrary.Merge(local, @base, disk);
        if (!merged.Equals(disk)) Update(merged);
        return merged;
    }

    /// <summary>Verschiebt ein Meeting in den Papierkorb (wiederherstellbar).</summary>
    public void Delete(Guid id)
    {
        if (Entry(id) == null) return;
        MeetingLibrary.MoveToTrash(CurrentFolder(id));
        Entries = _entries.Where(e => e.Id != id).ToList();
        _searchIndex.Remove(id);
    }

    /// <summary>Öffnet den Explorer mit dem Meeting-Ordner markiert.</summary>
    public void RevealInExplorer(Guid id)
    {
        string folder;
        try { folder = CurrentFolder(id); }
        catch (MeetingLibraryException) { return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("reveal meeting failed: " + ex.Message); }
    }

    /// <summary>Öffnet den Meeting-Ordner im Explorer (legt ihn bei Bedarf an).</summary>
    public void OpenLibraryFolder()
    {
        try
        {
            Directory.CreateDirectory(LibraryFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LibraryFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open meeting folder failed: " + ex.Message); }
    }

    /// <summary>Zieht alle Meetings aus <paramref name="oldFolder"/> in den aktuell eingestellten Meeting-Ordner um.</summary>
    public void MoveMeetings(string oldFolder)
    {
        try
        {
            Directory.CreateDirectory(LibraryFolder);
            foreach (var folder in MeetingLibrary.MeetingFolders(oldFolder))
                MoveDirectory(folder, MeetingLibrary.UniquePath(LibraryFolder, Path.GetFileName(folder)));
        }
        finally
        {
            Reload();
        }
    }

    /// <summary>Macht eine Umbenennung durch Claude rückgängig – nur, wenn seitdem niemand denselben Wert geändert hat.</summary>
    /// <exception cref="MeetingUndoException"/>
    public void Undo(ActivityEntry change)
    {
        if (change.MeetingIDs.Count == 0 || change.OldValue is not { } old) return;
        var id = change.MeetingIDs[0];
        var transcript = Load(id);
        switch (change.Action)
        {
            case ActivityAction.RenameMeeting:
                if (transcript.Title != change.NewValue) throw new MeetingUndoException();
                transcript.Title = old;
                break;
            case ActivityAction.RenameSpeaker:
                if (change.SpeakerID is not { } speaker || transcript.DisplayName(speaker) != change.NewValue)
                    throw new MeetingUndoException();
                if (old == speaker) transcript.SpeakerNames.Remove(speaker);
                else transcript.SpeakerNames[speaker] = old;
                break;
            default:
                return;
        }
        Update(transcript);
        ExternalChanges?.Invoke(new HashSet<Guid> { id });
    }

    // MARK: Geöffnete Meetings

    internal void Register(MeetingDocument document) => _openDocuments.Add(document);
    internal void Unregister(MeetingDocument document) => _openDocuments.Remove(document);

    /// <summary>Ausstehende Änderungen aller geöffneten Meetings sofort speichern (beim Beenden der App).</summary>
    public void FlushOpenDocuments()
    {
        foreach (var document in _openDocuments.ToList()) document.Flush();
    }

    // MARK: Intern

    /// <summary>Ordner eines Meetings. Hat ihn jemand von außen umbenannt (luna-mcp, Explorer), wird neu eingelesen.</summary>
    /// <exception cref="MeetingNotFoundException"/>
    private string CurrentFolder(Guid id)
    {
        if (Entry(id)?.Folder is { } folder && File.Exists(Path.Combine(folder, MeetingLibrary.TranscriptFileName)))
            return folder;
        Reload();
        return Entry(id)?.Folder ?? throw new MeetingNotFoundException();
    }

    private static List<MeetingEntry> ReadEntries(string parent, bool unsaved) =>
        MeetingLibrary.Scan(parent, unsaved, (folder, ex) =>
                Log.Write($"unreadable meeting {Path.GetFileName(folder)}: {ex.Message}"))
            .Select(m => EntryFor(m.Transcript, m.Folder, unsaved))
            .ToList();

    private string FullText(MeetingEntry entry)
    {
        if (_searchIndex.TryGetValue(entry.Id, out var cached)) return cached;
        string text;
        try { text = SearchText(MeetingLibrary.ReadTranscript(entry.Folder)); }
        catch (Exception) { text = ""; }
        _searchIndex[entry.Id] = text;
        return text;
    }

    private void MoveUnsavedIntoLibrary()
    {
        foreach (var folder in MeetingLibrary.MeetingFolders(UnsavedFolder))
        {
            try
            {
                MoveDirectory(folder, MeetingLibrary.UniquePath(LibraryFolder, Path.GetFileName(folder)));
                Log.Write("rescued meeting moved into library: " + Path.GetFileName(folder));
            }
            catch (Exception ex)
            {
                Log.Write("could not move rescued meeting: " + ex.Message);
            }
        }
    }

    /// <summary>Directory.Move kann nicht über Laufwerksgrenzen; dann kopieren und das Original erst danach entfernen.</summary>
    private static void MoveDirectory(string source, string target)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, target);
            return;
        }
        CopyDirectory(source, target);
        Directory.Delete(source, recursive: true);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private (DateTime, long) LogStamp()
    {
        try
        {
            var info = new FileInfo(_activityLog.Path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0);
        }
        catch (Exception) { return (DateTime.MinValue, 0); }
    }

    private void ApplyExternalChanges()
    {
        var changes = _activityLog.Entries(limit: 200)
            .Where(e => e.IsChange && JsonCoding.ToUtc(e.Date) > _lastActivity)
            .ToList();
        if (changes.Count == 0) return;
        _lastActivity = changes.Max(e => JsonCoding.ToUtc(e.Date));
        Reload();
        var ids = changes.SelectMany(e => e.MeetingIDs).ToHashSet();
        Log.Write($"external changes by Claude: {ids.Count} meeting(s)");
        ExternalChanges?.Invoke(ids);
    }

    private static MeetingEntry EntryFor(MeetingTranscript t, string folder, bool unsaved) =>
        new(t.Id, t.Title, t.Date, t.DurationSeconds, t.SpeakerIDs.Count, t.Summary != null, t.Confidential, folder, unsaved);

    private static string SearchText(MeetingTranscript t) =>
        string.Join("\n", new[] { t.Title, t.Summary?.Markdown ?? "" }.Concat(t.SpeakerNames.Values).Concat(t.Turns.Select(x => x.Text)))
            .ToLowerInvariant();

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
