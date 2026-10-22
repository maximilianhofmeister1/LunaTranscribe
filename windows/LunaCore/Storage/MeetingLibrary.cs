using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LunaCore;

/// <summary>Ein Meeting, wie es auf der Platte liegt: Inhalt, Ordner und die Revision der <c>meeting.json</c>.</summary>
/// <param name="Revision">Merkmal des gelesenen Dateistands; Schreibvorgänge von außen verlangen sie (optimistische Sperre).</param>
/// <param name="IsUnsaved">Liegt im Rettungsordner, weil der Meeting-Ordner nicht erreichbar war.</param>
public sealed record StoredMeeting(MeetingTranscript Transcript, string Folder, string Revision, bool HasAudio, bool IsUnsaved)
{
    public Guid Id => Transcript.Id;
}

/// <summary>Fehler der Meeting-Bibliothek (deutsche Meldungen für die Oberfläche und luna-mcp).</summary>
public abstract class MeetingLibraryException : Exception
{
    protected MeetingLibraryException(string message) : base(message) { }
}

public sealed class MeetingNotFoundException : MeetingLibraryException
{
    public MeetingNotFoundException()
        : base("Das Meeting wurde nicht gefunden – vielleicht wurde es im Explorer verschoben oder gelöscht.") { }
}

public sealed class RevisionMismatchException : MeetingLibraryException
{
    /// <summary>Revision des aktuellen Stands auf der Platte.</summary>
    public string Current { get; }

    public RevisionMismatchException(string current) : base("Das Meeting wurde inzwischen geändert.") => Current = current;
}

/// <summary>Papierkorb – austauschbar, damit Tests nichts in den echten Papierkorb legen.</summary>
public interface IRecycleBin
{
    /// <summary>Verschiebt einen Ordner wiederherstellbar in den Papierkorb (nie endgültig löschen).</summary>
    void MoveToRecycleBin(string folder);
}

/// <summary>Windows-Papierkorb über <c>Microsoft.VisualBasic.FileIO</c> (ohne WinForms/WPF).</summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    public static readonly WindowsRecycleBin Instance = new();

    public void MoveToRecycleBin(string folder) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
}

/// <summary>
/// Die Meeting-Bibliothek auf der Platte: ein Unterordner pro Meeting im frei wählbaren Meeting-Ordner.
/// <code>
/// &lt;Meeting-Ordner&gt;\2026-09-25 14-03 Planung Q4 · a1b2c3\
///     meeting.json   – Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit)
///     summary.md     – Zusammenfassung als Markdown (für Explorer, Vorschau, Claude CLI)
///     audio.wav      – nur mit „Aufnahme behalten“
/// </code>
/// App und luna-mcp lesen und schreiben ausschließlich über diese Funktionen. Beide Prozesse schreiben atomar;
/// Konflikte fangen Revisionen (luna-mcp) und <see cref="Merge"/> (App) ab. Format und Ordnernamen sind
/// identisch mit der macOS-Version.
/// </summary>
public static class MeetingLibrary
{
    public const string TranscriptFileName = "meeting.json";
    public const string SummaryFileName = "summary.md";
    public const string AudioFileName = "audio.wav";

    /// <summary>Papierkorb für <see cref="MoveToTrash"/>, wenn keiner übergeben wird.</summary>
    public static IRecycleBin RecycleBin { get; set; } = WindowsRecycleBin.Instance;

    // MARK: Lesen

    /// <summary>
    /// Unterordner mit einer <c>meeting.json</c>. Symbolische Links und Junctions werden übersprungen, damit nichts
    /// außerhalb des Meeting-Ordners gelesen oder verändert wird; ebenso versteckte Ordner.
    /// </summary>
    public static List<string> MeetingFolders(string parent)
    {
        try
        {
            return new DirectoryInfo(parent).EnumerateDirectories()
                .Where(d => d.LinkTarget is null
                            && !d.Name.StartsWith('.')
                            && (d.Attributes & FileAttributes.Hidden) == 0
                            && File.Exists(Path.Combine(d.FullName, TranscriptFileName)))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new();
        }
    }

    public static MeetingTranscript ReadTranscript(string folder) => Read(folder).Transcript;

    public static StoredMeeting Read(string folder, bool unsaved = false)
    {
        var data = File.ReadAllBytes(Path.Combine(folder, TranscriptFileName));
        var transcript = JsonCoding.Decode<MeetingTranscript>(data);
        var hasAudio = File.Exists(Path.Combine(folder, AudioFileName));
        return new StoredMeeting(transcript, folder, ContentHash.Hex(data), hasAudio, unsaved);
    }

    /// <summary>Alle lesbaren Meetings eines Ordners; unlesbare meldet <paramref name="onError"/>, ohne den Rest aufzuhalten.</summary>
    public static List<StoredMeeting> Scan(string parent, bool unsaved = false, Action<string, Exception>? onError = null)
    {
        var result = new List<StoredMeeting>();
        foreach (var folder in MeetingFolders(parent))
        {
            try { result.Add(Read(folder, unsaved)); }
            catch (Exception ex) { onError?.Invoke(folder, ex); }
        }
        return result;
    }

    // MARK: Schreiben

    /// <summary>
    /// Schreibt <c>meeting.json</c> (atomar) und <c>summary.md</c> und gibt den (ggf. umbenannten) Ordner zurück.
    /// Mit <paramref name="existing"/> wird der vorhandene Ordner aktualisiert und bei geändertem Titel umbenannt.
    /// </summary>
    public static string Write(MeetingTranscript transcript, string parent, string? existing)
    {
        Directory.CreateDirectory(parent);
        var wantedName = FolderName(transcript);

        string folder;
        if (existing is not null)
        {
            if (!File.Exists(Path.Combine(existing, TranscriptFileName))) throw new MeetingNotFoundException();
            folder = existing;
            var currentName = Path.GetFileName(Path.TrimEndingDirectorySeparator(existing));
            if (currentName != wantedName)
            {
                string renamed;
                if (string.Equals(currentName, wantedName, StringComparison.OrdinalIgnoreCase))
                {
                    // Nur Groß-/Kleinschreibung geändert: Windows sieht denselben Namen, also über einen Zwischennamen.
                    renamed = Path.Combine(parent, wantedName);
                    var temp = UniquePath(parent, wantedName + " ~");
                    Directory.Move(existing, temp);
                    Directory.Move(temp, renamed);
                }
                else
                {
                    renamed = UniquePath(parent, wantedName);
                    Directory.Move(existing, renamed);
                }
                folder = renamed;
            }
        }
        else
        {
            folder = UniquePath(parent, wantedName);
            Directory.CreateDirectory(folder);
        }

        AtomicFile.WriteAllBytes(Path.Combine(folder, TranscriptFileName), JsonCoding.EncodeToUtf8(transcript));

        var summaryPath = Path.Combine(folder, SummaryFileName);
        if (transcript.Summary is { } summary)
        {
            var markdown = $"# {transcript.Title} – Zusammenfassung\n\n{summary.Markdown}\n";
            AtomicFile.WriteAllBytes(summaryPath, Encoding.UTF8.GetBytes(markdown));
        }
        else if (File.Exists(summaryPath))
        {
            File.Delete(summaryPath);
        }
        return folder;
    }

    /// <summary>
    /// Schreibt eine Änderung nur, wenn die Datei noch den Stand <paramref name="expectedRevision"/> hat. Für Schreiber
    /// außerhalb der App (luna-mcp), die nicht mitbekommen, was die App gerade tut.
    /// </summary>
    /// <exception cref="MeetingNotFoundException"/>
    /// <exception cref="RevisionMismatchException"/>
    public static StoredMeeting Update(string folder, string expectedRevision, Action<MeetingTranscript> change)
    {
        var current = ReadExisting(folder);
        if (current.Revision != expectedRevision) throw new RevisionMismatchException(current.Revision);
        var transcript = current.Transcript;   // frisch gelesen, gehört uns allein
        change(transcript);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))!;
        var newFolder = Write(transcript, parent, folder);
        return Read(newFolder);
    }

    /// <summary>Verschiebt ein Meeting in den Papierkorb (wiederherstellbar) – nie endgültig löschen.</summary>
    /// <exception cref="MeetingNotFoundException"/>
    /// <exception cref="RevisionMismatchException"/>
    public static void MoveToTrash(string folder, string? expectedRevision = null, IRecycleBin? recycleBin = null)
    {
        var current = ReadExisting(folder);
        if (expectedRevision is not null && current.Revision != expectedRevision)
            throw new RevisionMismatchException(current.Revision);
        (recycleBin ?? RecycleBin).MoveToRecycleBin(folder);
    }

    // MARK: Zusammenführen

    /// <summary>
    /// Dreiwege-Zusammenführung für einen Speichervorgang der App, wenn die Datei seit dem Laden von außen geändert
    /// wurde (luna-mcp: Titel, Sprechernamen). Was der Nutzer lokal geändert hat, gewinnt; alles andere übernimmt
    /// den Stand der Platte. Die Eingaben bleiben unverändert.
    /// </summary>
    public static MeetingTranscript Merge(MeetingTranscript local, MeetingTranscript @base, MeetingTranscript disk)
    {
        var merged = local.Clone();
        if (local.Title == @base.Title) merged.Title = disk.Title;
        if (Equals(local.Summary, @base.Summary)) merged.Summary = disk.Summary;
        if (local.IsConfidential == @base.IsConfidential) merged.IsConfidential = disk.IsConfidential;
        if (local.Turns.SequenceEqual(@base.Turns)) merged.Turns = new List<MeetingTranscript.Turn>(disk.Turns);

        var names = new Dictionary<string, string>(disk.SpeakerNames);
        foreach (var key in local.SpeakerNames.Keys.Union(@base.SpeakerNames.Keys).ToList())
        {
            local.SpeakerNames.TryGetValue(key, out var localName);
            @base.SpeakerNames.TryGetValue(key, out var baseName);
            if (localName == baseName) continue;
            // Lokale Änderung gewinnt, auch ein Zurücksetzen.
            if (localName is null) names.Remove(key);
            else names[key] = localName;
        }
        merged.SpeakerNames = names;
        return merged;
    }

    // MARK: Namen

    /// <summary>„2026-09-25 14-03 Planung Q4 · a1b2c3“ – Datum zuerst, damit der Explorer chronologisch sortiert.</summary>
    public static string FolderName(MeetingTranscript t) =>
        $"{MeetingTranscript.Formatters.FileDate(t.Date)} {FileNames.Sanitized(t.Title)} · {ShortID(t.Id)}";

    /// <summary>Die ersten 6 Zeichen der ID, klein – so stehen sie auch im Ordnernamen.</summary>
    public static string ShortID(Guid id) => id.ToString("D")[..6].ToLowerInvariant();

    /// <summary>Freier Pfad <c>name</c>, sonst <c>name (2)</c>, <c>name (3)</c> …</summary>
    public static string UniquePath(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{name} ({n})");
        return candidate;
    }

    private static StoredMeeting ReadExisting(string folder)
    {
        if (!File.Exists(Path.Combine(folder, TranscriptFileName))) throw new MeetingNotFoundException();
        return Read(folder);
    }
}
