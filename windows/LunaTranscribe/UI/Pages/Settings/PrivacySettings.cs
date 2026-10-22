using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Output;
using LunaTranscribe.Search;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

/// <summary>
/// Einstellungen → Datenschutz: Verlauf, die Freigabe der Meetings für Claude (luna-mcp) mit Einrichtung,
/// Verbindungstest, Protokoll „Zuletzt durch Claude“ (mit „Rückgängig“ für Umbenennungen), Suchindex und
/// inhaltliche Suche, dazu die Mikrofon-Berechtigung. Gegenstück zu PrivacySettings.swift.
/// </summary>
public sealed class PrivacySettings : SettingsSectionView
{
    private bool _showNotice;
    private string? _copied;
    private string? _checkResult;
    private bool _checkFailed;
    private bool _checking;
    private string? _undoError;
    private List<ActivityEntry> _activity = new();
    private Dictionary<Guid, string> _titles = new();
    private DateTime _activityStamp;
    private LoadStatusView? _modelStatus;
    private readonly DispatcherTimer _activityTimer;

    public PrivacySettings(AppState state) : base(state)
    {
        _activityTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _activityTimer.Tick += (_, _) => ReloadActivityIfChanged();
    }

    protected override void Subscribe()
    {
        State.Search.PropertyChanged += OnSearchChanged;
        ReloadActivity();
        _activityTimer.Start();
    }

    protected override void Unsubscribe()
    {
        State.Search.PropertyChanged -= OnSearchChanged;
        _activityTimer.Stop();
    }

    /// <summary>Fortschritt des Modell-Downloads ohne Neuaufbau der Seite; alles andere baut neu auf.</summary>
    private void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchIndexer.Model) && State.Search.Model.Kind == SearchIndexer.ModelStateKind.Downloading
            && _modelStatus is not null)
        {
            _modelStatus.Status = ModelStatus();
            return;
        }
        OnExternalChange(sender, e);
    }

    protected override void Build(VStack root)
    {
        var s = State.Settings;

        root.Children.Add(Card("Local-First", IconKind.Shield,
            ToggleRow("Letzte Diktate im Verlauf zeigen",
                "Nur im Arbeitsspeicher, nie auf der Platte; weg, sobald die App beendet wird.",
                s.KeepHistory, v => s.KeepHistory = v),
            new LearnMore
            {
                Text = "Diktat-Audio bleibt ausschließlich im Arbeitsspeicher und wird direkt nach der Transkription verworfen. "
                       + "Meeting-Aufnahmen liegen während der Verarbeitung temporär unter %LOCALAPPDATA%\\LunaTranscribe und werden "
                       + "danach gelöscht (außer „Aufnahme behalten“). Transkripte liegen als JSON in deinem Meeting-Ordner. Ausnahmen "
                       + "vom Local-First-Prinzip sind nur die freiwillige Zusammenfassung über die Claude CLI und – wenn du sie "
                       + "freigibst – Meetings, die Claude über luna-mcp liest.",
            },
            s.IgnoredSuggestionKeys.Count > 0
                ? Row("Ignorierte Wörterbuch-Vorschläge", $"{s.IgnoredSuggestionKeys.Count} Korrekturen werden nicht mehr vorgeschlagen.",
                      Button("Zurücksetzen", () => { s.IgnoredSuggestionKeys = new List<string>(); Rebuild(); }))
                : null,
            s.SummaryCloudNoticeAccepted
                ? Row("Cloud-Hinweis für Zusammenfassungen", "Wurde bestätigt.",
                      Button("Wieder anzeigen", () => { s.SummaryCloudNoticeAccepted = false; Rebuild(); }))
                : null));

        root.Children.Add(ClaudeCard());

        root.Children.Add(Card("Berechtigungen", IconKind.CheckCircle,
            Row("Mikrofon", "Windows-Einstellungen → Datenschutz → Mikrofon → „Desktop-Apps den Zugriff erlauben“.",
                State.MicrophoneAllowed ? Granted() : Button("Erlauben…", Permissions.OpenMicrophoneSettings, "Luna.Primary"))));
    }

    // MARK: Claude

    private LunaCard ClaudeCard()
    {
        var s = State.Settings;
        var children = new List<UIElement?>
        {
            ToggleRow("Meetings für Claude freigeben",
                "Claude Code und Claude Desktop können deine Meetings über luna-mcp durchsuchen und lesen. Vertrauliche Meetings bleiben unsichtbar.",
                s.McpAccessEnabled, SetAccess),
        };
        if (_showNotice) children.Add(CloudNotice());

        if (s.McpAccessEnabled)
        {
            children.Add(ToggleRow("Claude darf Meetings bearbeiten",
                "Meetings umbenennen und Sprecher benennen. Löschen nur nach deiner Bestätigung in einem Dialog – in den Papierkorb.",
                s.McpWriteEnabled, v => { s.McpWriteEnabled = v; Rebuild(); }));
            if (s.McpWriteEnabled)
                children.Add(ToggleRow("Auch Umbenennungen bestätigen", "Zeigt vor jeder Umbenennung durch Claude einen Dialog.",
                    s.McpConfirmRenames, v => s.McpConfirmRenames = v));
            children.Add(Row("Claude Code", "Befehl einmal im Terminal ausführen (PowerShell oder Eingabeaufforderung).",
                CopyButton("Befehl kopieren", "code", ClaudeConnection.ClaudeCodeCommand)));
            children.Add(Row("Claude Desktop",
                $"In {ClaudeConnection.ClaudeDesktopConfigPath} unter „mcpServers“ einfügen, dann Claude Desktop neu starten.",
                CopyButton("Eintrag kopieren", "desktop", ClaudeConnection.ClaudeDesktopConfig)));
            var test = Button(_checking ? "Prüfe…" : "Testen", RunCheck);
            test.IsEnabled = !_checking;
            children.Add(Row("Verbindung testen", "Startet luna-mcp und prüft Meeting-Ordner, Freigaben, Suchindex und Modell.", test));
            if (_checkResult is { } result) children.Add(CheckOutput(result));
            if (!ClaudeConnection.HelperExists)
                children.Add(Error($"luna-mcp fehlt in dieser Installation ({ClaudeConnection.HelperPath})."));
            children.Add(ActivitySection());
            children.Add(SemanticSearchRow());
            if (State.Settings.SemanticSearchEnabled) children.Add(_modelStatus = new LoadStatusView { Status = ModelStatus(), Retry = State.Search.DownloadModel });
            else _modelStatus = null;
            children.Add(Row("Suchindex", IndexHint(), Button("Löschen", () => { State.Search.DeleteIndex(); Rebuild(); }, "Luna.Quiet")));
            if (State.Search.LastError is { } error) children.Add(Error(error));
        }
        else
        {
            _modelStatus = null;
        }

        children.Add(new LearnMore
        {
            Text = "luna-mcp ist ein kleines Programm in LunaTranscribe, das Claude Code oder Claude Desktop auf deinem PC starten. "
                   + "Es liest die Meetings direkt aus deinem Meeting-Ordner – ohne Netzwerk, auch wenn LunaTranscribe gerade nicht läuft. "
                   + "Was Claude liest (Suchtreffer, Transkripte), schickt Claude aber zur Verarbeitung an Anthropic, genau wie bei der "
                   + "Zusammenfassung. Audio und der Diktat-Verlauf sind nie erreichbar. Löschen geht nur nach deiner Zustimmung in einem "
                   + "Windows-Dialog, den Claude weder auslösen noch bestätigen kann. Jeder Zugriff steht im Protokoll oben.",
        });
        return Card("Meetings in Claude (MCP)", IconKind.Sparkles, children.ToArray());
    }

    /// <summary>Beim ersten Einschalten erst der Hinweis, was an Anthropic geht.</summary>
    private void SetAccess(bool enabled)
    {
        var s = State.Settings;
        if (enabled && !s.McpCloudNoticeAccepted)
        {
            _showNotice = true;
        }
        else
        {
            s.McpAccessEnabled = enabled;
            _showNotice = false;
        }
        Rebuild();
    }

    private FrameworkElement CloudNotice()
    {
        var stack = new VStack { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = "Meetings für Claude freigeben?", FontSize = 13.5 }.Styled("Luna.Text"));
        stack.Children.Add(new TextBlock
        {
            Text = "Die Transkription bleibt lokal. Wenn Claude über luna-mcp in deinen Meetings sucht oder sie liest, gehen diese "
                   + "Inhalte über dein Claude-Konto an Anthropic. Als vertraulich markierte Meetings sind ausgenommen. Du kannst die "
                   + "Freigabe jederzeit wieder ausschalten.",
            TextWrapping = TextWrapping.Wrap,
        }.Styled("Luna.Hint"));
        var buttons = new HStack { Spacing = 8 };
        buttons.Children.Add(Button("Freigeben", () =>
        {
            State.Settings.McpCloudNoticeAccepted = true;
            State.Settings.McpAccessEnabled = true;
            _showNotice = false;
            Rebuild();
        }, "Luna.Primary"));
        buttons.Children.Add(Button("Abbrechen", () => { _showNotice = false; Rebuild(); }));
        stack.Children.Add(buttons);
        return new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Child = stack }
            .Brushed(Border.BackgroundProperty, "Luna.SurfaceMuted");
    }

    private Button CopyButton(string title, string id, string text) =>
        Button(_copied == id ? "Kopiert ✓" : title, () =>
        {
            if (!ClaudeConnection.Copy(text)) return;
            _copied = id;
            Rebuild();
        }, icon: IconKind.Copy);

    private void RunCheck()
    {
        _checking = true;
        Rebuild();
        _ = ClaudeConnection.CheckAsync().ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _checking = false;
            (_checkFailed, _checkResult) = (!t.Result.Ok, t.Result.Output);
            Rebuild();
        }), TaskScheduler.Default);
    }

    private FrameworkElement CheckOutput(string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
        }.Styled("Luna.PlainTextBox");
        return box.Brushed(TextBox.ForegroundProperty, _checkFailed ? "Luna.Rose" : "Luna.InkMuted");
    }

    private FrameworkElement SemanticSearchRow() =>
        ToggleRow("Inhaltliche Suche",
            "Findet Stellen auch ohne die genauen Wörter („teurer werden“ zu „Preiserhöhung“). Lädt einmalig "
            + EmbeddingModelKinds.Standard.Label() + " und rechnet lokal auf der CPU.",
            State.Settings.SemanticSearchEnabled, v => { State.Settings.SemanticSearchEnabled = v; Rebuild(); });

    private LoadStatus ModelStatus()
    {
        var model = State.Search.Model;
        return model.Kind switch
        {
            SearchIndexer.ModelStateKind.Downloading => new LoadStatus(LoadPhase.Loading, $"Modell wird geladen… {model.Progress * 100:0} %", model.Progress),
            SearchIndexer.ModelStateKind.Ready => new LoadStatus(LoadPhase.Ready, "Modell liegt lokal – Suche nach Bedeutung ist aktiv."),
            SearchIndexer.ModelStateKind.Failed => new LoadStatus(LoadPhase.Failed, "Modell konnte nicht geladen werden: " + model.Error),
            SearchIndexer.ModelStateKind.NotDownloaded => new LoadStatus(LoadPhase.Warning, "Modell noch nicht geladen."),
            _ => new LoadStatus(LoadPhase.Idle, "Aus."),
        };
    }

    private string IndexHint()
    {
        if (State.Search.Stats is not { } stats)
            return State.Search.IsIndexing ? "Wird aufgebaut …" : "Wird beim nächsten Zugriff aufgebaut.";
        var semantic = State.Settings.SemanticSearchEnabled ? $", {stats.Embedded} inhaltlich erschlossen" : "";
        var running = State.Search.IsIndexing ? " Wird gerade aktualisiert." : "";
        return $"{stats.Meetings} Meetings, {stats.Chunks} Abschnitte{semantic}. Liegt nur auf diesem PC; Löschen baut ihn neu auf.{running}";
    }

    // MARK: Protokoll

    private FrameworkElement ActivitySection()
    {
        var stack = new VStack { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = "Zuletzt durch Claude", FontSize = 13.5 }.Styled("Luna.Text"));
        if (_activity.Count == 0) stack.Children.Add(Hint("Noch keine Zugriffe."));
        foreach (var entry in _activity)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var date = new TextBlock
            {
                Text = MeetingTranscript.Formatters.HeaderDate(entry.Date),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, "Luna.InkMuted");
            grid.Children.Add(date);
            var text = new TextBlock
            {
                Text = Describe(entry),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 36,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }.Styled("Luna.Caption");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (ActivityUndo.CanUndo(entry))
            {
                var undo = Button("Rückgängig", () => Undo(entry), "Luna.QuietSmall");
                undo.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(undo, 2);
                grid.Children.Add(undo);
            }
            stack.Children.Add(grid);
        }
        if (_undoError is { } error) stack.Children.Add(Error(error));
        return stack;
    }

    private string Describe(ActivityEntry entry)
    {
        var titles = entry.MeetingIDs.Select(id => _titles.TryGetValue(id, out var t) ? t : null).OfType<string>().ToList();
        var meeting = titles.Count > 0 ? $"„{titles[0]}“" : "ein Meeting";
        return entry.Action switch
        {
            ActivityAction.Read => $"{entry.Client} hat {meeting} gelesen",
            ActivityAction.Search => $"{entry.Client} hat gesucht ({entry.MeetingIDs.Count} Meeting(s) im Ergebnis)",
            ActivityAction.RenameMeeting => $"Umbenannt: „{entry.OldValue}“ → „{entry.NewValue}“",
            ActivityAction.RenameSpeaker => $"In {meeting}: „{entry.OldValue}“ → „{entry.NewValue}“",
            ActivityAction.Delete => $"{entry.MeetingIDs.Count} Meeting(s) in den Papierkorb verschoben (dort wiederherstellbar)",
            _ => "Löschen abgelehnt",
        };
    }

    private IEnumerable<string> LibraryFolders() =>
        new[] { AppPaths.MeetingFolder(State.Settings.MeetingFolder), AppPaths.UnsavedMeetings };

    private void Undo(ActivityEntry entry)
    {
        var folders = LibraryFolders().ToList();
        _ = Task.Run(() => ActivityUndo.Apply(entry, folders)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.Exception?.InnerException is { } error)
            {
                _undoError = error.Message;
                Log.Write("privacy: undo failed: " + error.Message);
            }
            else
            {
                _undoError = null;
                ClaudeActivity.NotifyMeetingChanged(entry.MeetingIDs[0]);
                State.Search.LibraryChanged();
            }
            ReloadActivity();
        }), TaskScheduler.Default);
    }

    private void ReloadActivityIfChanged()
    {
        DateTime stamp;
        try { stamp = File.Exists(AppPaths.McpActivityLog) ? File.GetLastWriteTimeUtc(AppPaths.McpActivityLog) : default; }
        catch (IOException) { return; }
        if (stamp != _activityStamp) ReloadActivity();
    }

    /// <summary>Protokoll und Meeting-Titel im Hintergrund lesen (die Bibliothek kann groß sein).</summary>
    private void ReloadActivity()
    {
        var folders = LibraryFolders().ToList();
        _ = Task.Run(() =>
        {
            var stamp = File.Exists(AppPaths.McpActivityLog) ? File.GetLastWriteTimeUtc(AppPaths.McpActivityLog) : default;
            var entries = new ActivityLog().Entries(40).Where(e => e.Action != ActivityAction.Search).Take(8).ToList();
            var titles = new Dictionary<Guid, string>();
            if (entries.Count > 0)
                foreach (var meeting in folders.SelectMany(f => MeetingLibrary.Scan(f)))
                    titles.TryAdd(meeting.Id, meeting.Transcript.Title);
            return (stamp, entries, titles);
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.IsFaulted) return;
            (_activityStamp, _activity, _titles) = t.Result;
            Rebuild();
        }), TaskScheduler.Default);
    }

    private static FrameworkElement Granted()
    {
        var row = new HStack { Spacing = 6 };
        row.Children.Add(new LunaIcon { Kind = IconKind.CheckCircle, Size = 14 }.Brushed(LunaIcon.ForegroundProperty, "Luna.Sage"));
        row.Children.Add(new TextBlock { Text = "Erteilt" }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, "Luna.Sage"));
        return row;
    }
}
