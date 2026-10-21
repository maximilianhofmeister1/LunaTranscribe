using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.UI.Components;
using LunaTranscribe.UI.Theme;
using Microsoft.Win32;

namespace LunaTranscribe.UI.Pages.Settings;

/// <summary>Einstellungen → Meetings (Pendant zu MeetingSettings.swift): Ordner, Aufnahme, Zusammenfassung mit Claude.</summary>
public sealed class MeetingSettings : SettingsSectionView
{
    // Überstehen einen Neuaufbau des Bereichs.
    private string? _cliCheck;
    private bool _cliCheckFailed;
    private bool _checking;
    private string? _folderError;

    public MeetingSettings(AppState state) : base(state) { }

    /// <summary>Fortschritt der Warteschlange und neue Meetings ändern hier nichts – kein Neuaufbau (Fokus bleibt im Feld).</summary>
    protected override bool Affects(object? sender, string? property) =>
        base.Affects(sender, property)
        && property is not (nameof(AppState.MeetingsPending) or nameof(AppState.MeetingQueueItems)
            or nameof(AppState.MeetingCount) or nameof(AppState.RecentMeetings));

    protected override void Build(VStack root)
    {
        var s = State.Settings;

        // Meeting-Ordner
        var choose = Button("Wählen…", ChooseFolder);
        var folderControls = string.IsNullOrEmpty(s.MeetingFolder)
            ? new FrameworkElement[] { choose }
            : new FrameworkElement[] { choose, Button("Standard", () => ChangeFolder(""), "Luna.Quiet") };
        root.Children.Add(Card("Meeting-Ordner", IconKind.Folder,
            Row(State.Meetings.LibraryFolder,
                "Hier liegt pro Meeting ein Ordner mit meeting.json, summary.md und – falls behalten – der Aufnahme.", folderControls),
            _folderError != null ? Error(_folderError) : null,
            ToggleRow("Aufnahme (WAV) im Meeting-Ordner behalten", "Sonst wird die Aufnahme nach der Verarbeitung gelöscht.",
                s.MeetingKeepAudio, v => s.MeetingKeepAudio = v)));

        // Aufnahme
        int[] counts = { 0, 2, 3, 4, 5, 6, 7, 8 };
        root.Children.Add(Card("Aufnahme", IconKind.Waveform,
            ToggleRow("Systemaudio (Zoom, Teams, Browser) zusätzlich zum Mikrofon aufnehmen", null,
                s.MeetingCaptureSystemAudio, v => s.MeetingCaptureSystemAudio = v),
            Row("Personen im Meeting", "Eine bekannte Teilnehmerzahl verbessert die Sprechererkennung deutlich. Auch im Menü änderbar.",
                Picker(counts, counts.Contains(s.MeetingSpeakerCount) ? s.MeetingSpeakerCount : 0, SpeakerCountLabel,
                    v => s.MeetingSpeakerCount = v, width: 190)),
            new LearnMore
            {
                Text = "Während des Meetings wird nur aufgenommen; Sprecher-Erkennung und Transkription laufen lokal nach „Beenden“. " +
                       "Für Online-Meetings Kopfhörer nutzen, sonst hört das Mikrofon die Lautsprecher mit. Systemaudio wird vom " +
                       "Standard-Ausgabegerät mitgeschnitten (WASAPI-Loopback) – dafür braucht Windows keine eigene Berechtigung.",
            }));

        // ── Integration: Karte „Inhaltliche Suche“ (Suchindex, Embedding-Modell) gehört zum Suchindex und kommt hier hin. ──
        if (SemanticSearchCard() is { } search) root.Children.Add(search);

        // Zusammenfassung mit Claude
        var cliPath = new TextBox { Text = s.ClaudeCliPath, Width = 220 };
        Luna.SetPlaceholder(cliPath, "automatisch");
        cliPath.TextChanged += (_, _) => Quietly(() => s.ClaudeCliPath = cliPath.Text);
        var test = Button(_checking ? "Prüfe…" : "Testen", CheckCli);
        test.IsEnabled = !_checking;

        var model = new TextBox { Text = s.ClaudeModel, Width = 220 };
        Luna.SetPlaceholder(model, "Standard");
        model.TextChanged += (_, _) => Quietly(() => s.ClaudeModel = model.Text);

        var prompt = new VStack { Spacing = 6 };
        prompt.Children.Add(new TextBlock { Text = "Anweisung für die Zusammenfassung", FontSize = 13.5 }.Styled("Luna.Text"));
        prompt.Children.Add(PromptEditor(s.SummaryPrompt, s.SummaryPromptIsDefault, v => s.SummaryPrompt = v,
            () => s.SummaryPrompt = SummaryPrompt.DefaultTemplate));
        prompt.Children.Add(Hint("„{language}“ wird durch die Sprache des Meetings ersetzt. Das Transkript hängt die App mit Zeitstempeln und Sprechernamen an."));

        root.Children.Add(Card("Zusammenfassung mit Claude", IconKind.Sparkles,
            CloudNote(),
            Row("Claude CLI", @"Leer = automatisch suchen (.local\bin im Benutzerordner, npm, Programme, PATH).", cliPath, test),
            _cliCheck != null ? CheckResult() : null,
            Row("Modell", "Alias wie „sonnet“ oder „opus“; leer = Standard der CLI.", model),
            prompt));
    }

    /// <summary>Platzhalter für die Karte „Inhaltliche Suche“ (Integration Suchindex); null = keine Karte.</summary>
    private UIElement? SemanticSearchCard() => null;

    private static string SpeakerCountLabel(int count) => count == 0 ? "Automatisch" : $"{count} Personen";

    private static FrameworkElement CloudNote()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new LunaIcon { Kind = IconKind.Cloud, Size = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) }
            .Brushed(LunaIcon.ForegroundProperty, "Luna.InkMuted"));
        var text = new TextBlock
        {
            Text = "Die Transkription bleibt lokal. Nur wenn du bei einem Meeting „Zusammenfassen“ klickst, geht dessen Text über die Claude CLI an Anthropic.",
        }.Styled("Luna.Caption");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private FrameworkElement CheckResult()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var brush = _cliCheckFailed ? "Luna.Rose" : "Luna.Sage";
        grid.Children.Add(new LunaIcon { Kind = _cliCheckFailed ? IconKind.ErrorCircle : IconKind.CheckCircle, Size = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) }
            .Brushed(LunaIcon.ForegroundProperty, brush));
        var text = new TextBlock { Text = _cliCheck }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, brush);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    // MARK: Aktionen

    private async void CheckCli()
    {
        _checking = true;
        Rebuild();
        try
        {
            _cliCheck = "Gefunden: " + await State.Summarizer.CheckCliAsync();
            _cliCheckFailed = false;
        }
        catch (Exception ex)
        {
            _cliCheck = ex.Message;
            _cliCheckFailed = true;
        }
        _checking = false;
        Rebuild();
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Als Meeting-Ordner verwenden",
            Multiselect = false,
        };
        if (Directory.Exists(State.Meetings.LibraryFolder)) dialog.InitialDirectory = State.Meetings.LibraryFolder;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ChangeFolder(dialog.FolderName);
    }

    /// <summary>Wechselt den Meeting-Ordner und fragt, ob vorhandene Meetings mitziehen sollen.</summary>
    private void ChangeFolder(string newPath)
    {
        _folderError = null;
        var store = State.Meetings;
        var oldFolder = store.LibraryFolder;
        if (SamePath(AppPaths.MeetingFolder(newPath), oldFolder)) { Rebuild(); return; }

        var moveExisting = false;
        var saved = store.Entries.Count(e => !e.IsUnsaved);
        if (saved > 0)
        {
            var choice = LunaDialog.Show(this, "Vorhandene Meetings verschieben?",
                $"Im bisherigen Ordner liegen {saved} Meetings. Sollen sie in den neuen Ordner umziehen? Sonst bleiben sie dort und " +
                "erscheinen erst wieder, wenn du zurückwechselst.",
                new DialogButton("Verschieben", "Luna.Primary", IsDefault: true),
                new DialogButton("Nur neuen Ordner nutzen"),
                new DialogButton("Abbrechen", "Luna.Quiet"));
            if (choice is < 0 or 2) return;
            moveExisting = choice == 0;
        }

        State.Settings.MeetingFolder = newPath;   // lädt die Bibliothek neu
        if (moveExisting)
        {
            try { store.MoveMeetings(oldFolder); }
            catch (Exception ex) { _folderError = "Verschieben fehlgeschlagen: " + ex.Message; }
        }
        Rebuild();
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
