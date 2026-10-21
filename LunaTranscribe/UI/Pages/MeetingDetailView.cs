using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Meeting;
using LunaTranscribe.UI.Components;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>
/// Ein Meeting: Titel, Aktionen (Zusammenfassen, Export, Explorer, Löschen) und zwei Tabs – Transkript (Sprecher
/// benennen, Beiträge korrigieren) und Zusammenfassung (Pendant zu MeetingDetailView.swift). Besitzt das
/// <see cref="MeetingDocument"/> und gibt es in <see cref="Close"/> frei (speichert Ausstehendes).
/// </summary>
public sealed class MeetingDetailView : UserControl
{
    /// <summary>Nur für UI-Snapshots: mit dem Tab „Zusammenfassung“ öffnen.</summary>
    internal static bool StartOnSummaryTab;

    private readonly AppState _state;
    private readonly MeetingDocument _doc;
    private bool _showSummary = StartOnSummaryTab;
    private string? _actionMessage;
    private bool _actionFailed;
    private bool _updatingTitle;

    private readonly TextBox _title;
    private readonly WrapPanel _chips = new();
    private readonly ContentControl _saveError = new() { Focusable = false };
    private readonly ContentControl _actions = new() { Focusable = false };
    private readonly ContentControl _tab = new() { Focusable = false };
    private readonly RadioButton _transcriptTab;
    private readonly RadioButton _summaryTab;
    /// <summary>Sprecher-Beschriftungen der Beiträge – folgen dem Namen live, ohne Neuaufbau.</summary>
    private readonly List<(string SpeakerId, TextBlock Label)> _speakerLabels = new();

    public Guid MeetingId => _doc.Id;

    public MeetingDetailView(AppState state, MeetingDocument document)
    {
        _state = state;
        _doc = document;

        _title = new TextBox
        {
            FontSize = 28, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxLines = 3, Margin = new Thickness(-2, 0, 0, 0),
            Text = _doc.Transcript.Title,
        }.Styled("Luna.PlainTextBox");
        _title.SetResourceReference(FontFamilyProperty, "Luna.Font.Display");
        Luna.SetPlaceholder(_title, "Titel");
        _title.TextChanged += (_, _) => { if (!_updatingTitle) _doc.SetTitle(_title.Text); };
        _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };

        _transcriptTab = new RadioButton { Content = "Transkript", GroupName = "MeetingTab" + Guid.NewGuid().ToString("N"), IsChecked = !_showSummary }.Styled("Luna.Segment");
        _summaryTab = new RadioButton { Content = "Zusammenfassung", GroupName = _transcriptTab.GroupName, IsChecked = _showSummary }.Styled("Luna.Segment");
        _transcriptTab.Checked += (_, _) => { _showSummary = false; BuildTab(); };
        _summaryTab.Checked += (_, _) => { _showSummary = true; BuildTab(); };
        var segments = new UniformGrid { Columns = 2 };
        segments.Children.Add(_transcriptTab);
        segments.Children.Add(_summaryTab);
        var segmentBar = new Border
        {
            Padding = new Thickness(3), CornerRadius = new CornerRadius(9), Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Child = segments,
        }.Brushed(Border.BackgroundProperty, "Luna.SurfaceMuted");

        var header = new VStack { Spacing = 8 };
        header.Children.Add(_title);
        header.Children.Add(_chips);
        header.Children.Add(_saveError);

        var body = new VStack { Spacing = 18 };
        body.Children.Add(header);
        body.Children.Add(_actions);
        body.Children.Add(segmentBar);
        body.Children.Add(_tab);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            Content = new Border { Margin = new Thickness(28, 36, 28, 28), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch, Child = body },
        };
        scroll.SetResourceReference(BackgroundProperty, "Luna.Background");
        Content = scroll;

        RefreshHeader();
        BuildActions();
        BuildTab();

        Loaded += (_, _) =>
        {
            _doc.PropertyChanged += OnDocumentChanged;
            _doc.Replaced += OnReplaced;
            _state.Summarizer.PropertyChanged += OnSummarizerChanged;
        };
        Unloaded += (_, _) =>
        {
            _doc.PropertyChanged -= OnDocumentChanged;
            _doc.Replaced -= OnReplaced;
            _state.Summarizer.PropertyChanged -= OnSummarizerChanged;
        };
    }

    /// <summary>Speichert Ausstehendes und gibt das Dokument frei.</summary>
    public void Close() => _doc.Dispose();

    private MeetingTranscript Transcript => _doc.Transcript;
    private bool IsSummarizing => _state.Summarizer.IsRunning(_doc.Id);

    // MARK: Änderungen

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MeetingDocument.Transcript):
                RefreshHeader();
                BuildActions();
                foreach (var (id, label) in _speakerLabels) label.Text = Transcript.DisplayName(id);
                break;
            case nameof(MeetingDocument.SaveError):
            case nameof(MeetingDocument.IsDeleted):
                RefreshHeader();
                break;
            case nameof(MeetingDocument.LearnedSuggestions):
                if (!_showSummary) BuildTab();
                break;
        }
    }

    /// <summary>Inhalt von außen ersetzt (Claude, Zusammenführung, neue Zusammenfassung): alles neu, Titel nur ohne Fokus.</summary>
    private void OnReplaced()
    {
        if (!_title.IsKeyboardFocusWithin && _title.Text != Transcript.Title)
        {
            _updatingTitle = true;
            _title.Text = Transcript.Title;
            _updatingTitle = false;
        }
        RefreshHeader();
        BuildActions();
        BuildTab();
    }

    private void OnSummarizerChanged(object? sender, PropertyChangedEventArgs e)
    {
        BuildActions();
        if (_showSummary) BuildTab();
    }

    // MARK: Kopf

    private void RefreshHeader()
    {
        var t = Transcript;
        _chips.Children.Clear();
        AddChip(LunaDates.LongDateTime(t.Date), IconKind.Calendar);
        AddChip(MeetingTranscript.Clock(t.DurationSeconds), IconKind.Clock);
        AddChip($"{t.WordCount} Wörter", IconKind.TextLines);
        AddChip($"{t.Engine.ShortLabel()} · {t.Language.DisplayName()}", IconKind.Cpu);

        var confidential = new ToggleButton
        {
            Content = "Vertraulich", IsChecked = t.Confidential, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Vertrauliche Meetings sieht Claude nicht (luna-mcp), sie landen nicht im Suchindex und werden nicht über Claude zusammengefasst.",
        }.Styled("Luna.ToggleQuiet");
        Luna.SetIcon(confidential, t.Confidential ? IconKind.Lock : IconKind.Unlock);
        confidential.Click += (_, _) => _doc.SetConfidential(confidential.IsChecked == true);
        _chips.Children.Add(confidential);

        _saveError.Content = _doc.SaveError is { } error ? IconLine(error, IconKind.Warning, "Luna.Rose") : null;
    }

    private void AddChip(string text, IconKind icon)
    {
        var chip = MeetingRow.Chip(text, icon, "Luna.InkMuted");
        chip.Margin = new Thickness(0, 0, 8, 6);
        chip.VerticalAlignment = VerticalAlignment.Center;
        _chips.Children.Add(chip);
    }

    // MARK: Aktionen

    private void BuildActions()
    {
        var t = Transcript;
        var left = new HStack { Spacing = 8 };
        if (IsSummarizing)
        {
            var cancel = new Button { Content = "Zusammenfassung abbrechen" }.Styled("Luna.Secondary");
            Luna.SetIcon(cancel, IconKind.Stop);
            cancel.Click += (_, _) => _state.Summarizer.Cancel(_doc.Id);
            left.Children.Add(cancel);
            left.Children.Add(new Spinner { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var summarize = new Button
            {
                Content = t.Summary == null ? "Zusammenfassen" : "Neu zusammenfassen", IsEnabled = !t.Confidential && !_doc.IsDeleted,
            }.Styled("Luna.Primary");
            Luna.SetIcon(summarize, IconKind.Sparkles);
            // Deaktivierte Knöpfe zeigen ihren Tooltip nur so.
            ToolTipService.SetShowOnDisabled(summarize, true);
            summarize.ToolTip = t.Confidential
                ? "Vertrauliche Meetings werden nicht an Claude geschickt."
                : "Fasst das Meeting über die Claude CLI zusammen (Text geht an Anthropic)";
            summarize.Click += (_, _) => RequestSummary();
            left.Children.Add(summarize);
        }

        var export = new Button { Content = "Exportieren" }.Styled("Luna.Secondary");
        Luna.SetIcon(export, IconKind.Share);
        export.ContextMenu = ExportMenu();
        export.Click += (_, _) => MenuButton.Open(export);
        left.Children.Add(export);

        var folder = new Button { ToolTip = "Im Explorer zeigen", Padding = new Thickness(10, 7, 10, 7) }.Styled("Luna.Secondary");
        Luna.SetIcon(folder, IconKind.Folder);
        folder.Click += (_, _) => _state.Meetings.RevealInExplorer(_doc.Id);
        left.Children.Add(folder);

        var delete = new Button { ToolTip = "Meeting löschen", Padding = new Thickness(10, 7, 10, 7) }.Styled("Luna.Destructive");
        Luna.SetIcon(delete, IconKind.Trash);
        delete.Click += (_, _) => Delete();

        var row = new DockPanel();
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(left);

        var stack = new VStack { Spacing = 6 };
        stack.Children.Add(row);
        if (_actionMessage != null)
            stack.Children.Add(new TextBlock { Text = _actionMessage, FontSize = 11.5 }.Styled("Luna.Caption")
                .Brushed(TextBlock.ForegroundProperty, _actionFailed ? "Luna.Rose" : "Luna.Sage"));
        _actions.Content = stack;
    }

    private ContextMenu ExportMenu()
    {
        var menu = new ContextMenu();
        foreach (var format in new[] { MeetingTranscript.ExportFormat.Text, MeetingTranscript.ExportFormat.Markdown })
        {
            menu.Items.Add(MenuItem($"Transkript als {format.Label()}…", () => Export(format, includeSummary: false)));
            if (Transcript.Summary != null)
                menu.Items.Add(MenuItem($"Transkript + Zusammenfassung als {format.Label()}…", () => Export(format, includeSummary: true)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Als Markdown kopieren", () =>
        {
            MeetingExporter.CopyToClipboard(Transcript, includeSummary: Transcript.Summary != null);
            ShowMessage("In die Zwischenablage kopiert.");
        }));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private void RequestSummary()
    {
        _summaryTab.IsChecked = true;
        if (!_state.Settings.SummaryCloudNoticeAccepted)
        {
            var choice = LunaDialog.Show(this, "Zusammenfassung mit Claude",
                "Die Transkription bleibt lokal. Für die Zusammenfassung schickt LunaTranscribe den Text des Meetings über die " +
                "Claude CLI an Anthropic – mit deinem Claude-Konto. Dieser Hinweis erscheint nur einmal.",
                new DialogButton("Verstanden, zusammenfassen", "Luna.Primary", IsDefault: true),
                new DialogButton("Abbrechen"));
            if (choice != 0) return;
            _state.Settings.SummaryCloudNoticeAccepted = true;
        }
        _doc.Flush();   // aktuelle Namen/Korrekturen mitschicken
        _state.Summarizer.Summarize(_doc.Transcript);
    }

    private void Export(MeetingTranscript.ExportFormat format, bool includeSummary)
    {
        try
        {
            var path = MeetingExporter.SaveWithDialog(Transcript, format, includeSummary, _state.Meetings.LibraryFolder, Window.GetWindow(this));
            if (path != null) ShowMessage("Exportiert: " + System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowMessage("Export fehlgeschlagen: " + ex.Message, failed: true);
        }
    }

    private void Delete()
    {
        var ok = LunaDialog.Confirm(this, $"„{Transcript.Title}“ löschen?",
            "Der Meeting-Ordner (Transkript, Zusammenfassung, ggf. Aufnahme) wandert in den Papierkorb.", "In den Papierkorb legen");
        if (!ok) return;
        try
        {
            _doc.Flush();
            _state.Meetings.Delete(_doc.Id);
            _state.Router.SelectedMeetingId = _state.Meetings.Entries.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            ShowMessage("Löschen fehlgeschlagen: " + ex.Message, failed: true);
        }
    }

    private void ShowMessage(string message, bool failed = false)
    {
        _actionMessage = message;
        _actionFailed = failed;
        BuildActions();
    }

    // MARK: Tabs

    private void BuildTab()
    {
        _speakerLabels.Clear();
        _tab.Content = _showSummary ? SummaryTab() : TranscriptTab();
    }

    private FrameworkElement TranscriptTab()
    {
        var t = Transcript;
        var stack = new VStack { Spacing = 14 };
        stack.Children.Add(SpeakersCard());

        if (_doc.LearnedSuggestions > 0)
        {
            var hint = IconLine($"{VocabularySuggestion.CountLabel(_doc.LearnedSuggestions)} aus deiner Korrektur – ansehen", IconKind.Book, "Luna.Honey");
            hint.OnClick(() => _state.OpenMainWindow(MainPage.Dictionary));
            stack.Children.Add(hint);
        }

        var turns = new VStack { Spacing = 4 };
        var speakerIds = t.SpeakerIDs;
        foreach (var turn in t.Turns)
        {
            var color = LunaTheme.SpeakerBrush(Math.Max(0, speakerIds.IndexOf(turn.SpeakerID)));
            var row = new TurnRow(turn, t.DisplayName(turn.SpeakerID), color, text => _doc.CorrectTurn(turn.Id, text));
            _speakerLabels.Add((turn.SpeakerID, row.SpeakerLabel));
            turns.Children.Add(row);
        }
        stack.Children.Add(new LunaCard { Padding = new Thickness(10), Content = turns });
        return stack;
    }

    private FrameworkElement SpeakersCard()
    {
        var t = Transcript;
        var wrap = new WrapPanel();
        var ids = t.SpeakerIDs;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var item = new StackPanel { Orientation = Orientation.Horizontal, Width = 260, Margin = new Thickness(0, 0, 12, 8) };
            item.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = LunaTheme.SpeakerBrush(i), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var label = new TextBlock { Text = id, FontSize = 12, Width = 72, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Styled("Luna.Caption");
            label.TextWrapping = TextWrapping.NoWrap;
            item.Children.Add(label);
            var name = new TextBox { Width = 160, Text = t.SpeakerNames.TryGetValue(id, out var n) ? n : "" };
            Luna.SetPlaceholder(name, "Name");
            name.TextChanged += (_, _) => _doc.SetSpeakerName(id, name.Text);
            item.Children.Add(name);
            wrap.Children.Add(item);
        }
        var stack = new VStack { Spacing = 10 };
        stack.Children.Add(new SectionTitle { Title = "Sprecher", Icon = IconKind.People });
        stack.Children.Add(wrap);
        return new LunaCard { Padding = new Thickness(16, 16, 16, 8), Content = stack };
    }

    private FrameworkElement SummaryTab()
    {
        var t = Transcript;
        var stack = new VStack { Spacing = 12 };
        if (_state.Summarizer.Error(_doc.Id) is { } error)
            stack.Children.Add(new LunaCard { Content = IconLine(error, IconKind.ErrorCircle, "Luna.Rose") });

        if (IsSummarizing)
        {
            var row = new HStack { Spacing = 10 };
            row.Children.Add(new Spinner { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "Claude fasst das Meeting zusammen … das dauert je nach Länge 10–60 Sekunden.", VerticalAlignment = VerticalAlignment.Center }
                .Styled("Luna.TextMuted"));
            stack.Children.Add(new LunaCard { Content = row });
        }

        if (t.Summary is { } summary)
        {
            var content = new VStack { Spacing = 10 };
            content.Children.Add(new MarkdownView(summary.Markdown));
            content.Children.Add(new Separator { Margin = new Thickness(0, 4, 0, 0) });
            var model = summary.Model is { Length: > 0 } m ? $" ({m})" : "";
            content.Children.Add(new TextBlock { Text = $"Erstellt {LunaDates.DateTime(summary.CreatedAt)} mit Claude{model}", FontSize = 11 }.Styled("Luna.Caption"));
            stack.Children.Add(new LunaCard { Padding = new Thickness(20), Content = content });
        }
        else if (!IsSummarizing)
        {
            var content = new VStack { Spacing = 10 };
            content.Children.Add(new SectionTitle { Title = "Noch keine Zusammenfassung", Icon = IconKind.Sparkles });
            content.Children.Add(new TextBlock
            {
                Text = "„Zusammenfassen“ schickt das Transkript über die Claude CLI an Claude und legt Kurzfassung, Themen, Entscheidungen, " +
                       "Aufgaben und offene Fragen hier ab. Die Anweisung passt du unter Einstellungen → Meetings an.",
            }.Styled("Luna.Hint"));
            var adjust = new Button { Content = "Anweisung anpassen…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0) }.Styled("Luna.Quiet");
            adjust.Click += (_, _) => _state.OpenSettings(SettingsSection.Meetings);
            content.Children.Add(adjust);
            stack.Children.Add(new LunaCard { Content = content });
        }
        return stack;
    }

    // MARK: Helfer

    private static FrameworkElement IconLine(string text, IconKind icon, string brush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var i = new LunaIcon { Kind = icon, Size = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 7, 0) }.Brushed(LunaIcon.ForegroundProperty, brush);
        grid.Children.Add(i);
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Medium }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, brush);
        Grid.SetColumn(t, 1);
        grid.Children.Add(t);
        return grid;
    }

    /// <summary>Ein Redebeitrag; per Stift-Knopf (beim Überfahren) bearbeitbar. „Übernehmen“ meldet den neuen Text zurück.</summary>
    private sealed class TurnRow : Border
    {
        public TextBlock SpeakerLabel { get; }

        private readonly MeetingTranscript.Turn _turn;
        private readonly Action<string> _onCorrect;
        private readonly Button _pencil;
        private readonly ContentControl _body = new() { Focusable = false };
        private bool _editing;
        private string _text;

        public TurnRow(MeetingTranscript.Turn turn, string speaker, Brush color, Action<string> onCorrect)
        {
            _turn = turn;
            _text = turn.Text;
            _onCorrect = onCorrect;
            Padding = new Thickness(6);
            CornerRadius = new CornerRadius(8);
            Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Rectangle { Fill = color, RadiusX = 1.5, RadiusY = 1.5 });

            var content = new VStack { Spacing = 3, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);

            var head = new DockPanel { LastChildFill = false };
            SpeakerLabel = new TextBlock { Text = speaker, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = color, VerticalAlignment = VerticalAlignment.Center }
                .Styled("Luna.TextBase");
            SpeakerLabel.Foreground = color;
            head.Children.Add(SpeakerLabel);
            var clock = new TextBlock { Text = MeetingTranscript.Clock(turn.Start), FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                .Styled("Luna.Caption");
            head.Children.Add(clock);
            _pencil = new Button { ToolTip = "Korrigieren – das Wörterbuch lernt daraus", Visibility = Visibility.Hidden, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, -3, 0, -3) }
                .Styled("Luna.Quiet");
            Luna.SetIcon(_pencil, IconKind.Pencil);
            _pencil.Click += (_, _) => StartEditing();
            DockPanel.SetDock(_pencil, Dock.Right);
            head.Children.Add(_pencil);
            content.Children.Add(head);
            content.Children.Add(_body);
            Child = grid;
            ShowText();

            MouseEnter += (_, _) => UpdateHover(true);
            MouseLeave += (_, _) => UpdateHover(false);
        }

        private void UpdateHover(bool hovering)
        {
            _pencil.Visibility = hovering && !_editing ? Visibility.Visible : Visibility.Hidden;
            if (hovering && !_editing) SetResourceReference(BackgroundProperty, "Luna.SurfaceMutedHover");
            else Background = Brushes.Transparent;
        }

        private void ShowText()
        {
            // Schreibgeschütztes Feld statt TextBlock, damit sich Text markieren und kopieren lässt.
            var text = new TextBox
            {
                Text = _text, IsReadOnly = true, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Cursor = Cursors.IBeam,
                Margin = new Thickness(-2, 0, 0, 0),   // Innenabstand des Textfelds ausgleichen, bündig mit dem Sprechernamen
            }.Styled("Luna.PlainTextBox");
            _body.Content = text;
        }

        private void StartEditing()
        {
            _editing = true;
            UpdateHover(false);
            var editor = new TextBox { Text = _text, MinHeight = 60, FontSize = 13.5 }.Styled("Luna.Editor");
            var apply = new Button { Content = "Übernehmen" }.Styled("Luna.Primary");
            var cancel = new Button { Content = "Abbrechen" }.Styled("Luna.Quiet");
            apply.Click += (_, _) =>
            {
                var cleaned = editor.Text.Trim();
                if (cleaned.Length > 0 && cleaned != _text)
                {
                    _text = cleaned;
                    _onCorrect(cleaned);
                }
                StopEditing();
            };
            cancel.Click += (_, _) => StopEditing();
            editor.KeyDown += (_, e) => { if (e.Key == Key.Escape) StopEditing(); };
            var buttons = new HStack { Spacing = 6 };
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            var stack = new VStack { Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(editor);
            stack.Children.Add(buttons);
            _body.Content = stack;
            editor.Loaded += (_, _) => { editor.Focus(); editor.CaretIndex = editor.Text.Length; };
        }

        private void StopEditing()
        {
            _editing = false;
            ShowText();
            UpdateHover(IsMouseOver);
        }
    }
}
