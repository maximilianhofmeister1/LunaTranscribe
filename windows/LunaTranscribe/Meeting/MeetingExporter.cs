using System.IO;
using System.Text;
using System.Windows;
using LunaCore;
using LunaTranscribe.UI.Theme;
using Microsoft.Win32;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Export eines Meetings als TXT oder Markdown über den „Speichern unter“-Dialog. Gespeichert wird ein Meeting
/// ohnehin als JSON in der Bibliothek (<see cref="MeetingStore"/>); Export ist nur für die Weitergabe.
/// </summary>
public static class MeetingExporter
{
    /// <summary>Gibt den gewählten Pfad zurück, null bei Abbruch.</summary>
    /// <exception cref="IOException"/>
    public static string? SaveWithDialog(MeetingTranscript transcript, MeetingTranscript.ExportFormat format,
                                         bool includeSummary, string startFolder, Window? owner = null)
    {
        var ext = format.FileExtension();
        var dialog = new SaveFileDialog
        {
            Title = "Meeting exportieren",
            FileName = $"{transcript.SuggestedFileName}.{ext}",
            DefaultExt = "." + ext,
            AddExtension = true,
            Filter = format == MeetingTranscript.ExportFormat.Text ? "Text (*.txt)|*.txt" : "Markdown (*.md)|*.md",
            OverwritePrompt = true,
        };
        if (Directory.Exists(startFolder)) dialog.InitialDirectory = startFolder;
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return null;
        // UTF-8 ohne BOM wie in der macOS-Version.
        File.WriteAllText(dialog.FileName, transcript.Render(format, includeSummary), new UTF8Encoding(false));
        return dialog.FileName;
    }

    public static void CopyToClipboard(MeetingTranscript transcript, bool includeSummary) =>
        UiHelpers.CopyToClipboard(transcript.Render(MeetingTranscript.ExportFormat.Markdown, includeSummary));
}
