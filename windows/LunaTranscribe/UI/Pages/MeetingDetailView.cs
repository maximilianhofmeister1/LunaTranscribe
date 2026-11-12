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
/// Ein Meeting: Titel, Aktionen (Zusammenfassen in wählbarer Sprache, Export, Explorer, Löschen) und zwei Tabs –
/// Transkript (Sprecher benennen, Beiträge korrigieren) und Zusammenfassung (lesen, bearbeiten mit Strg+E / Strg+S /
/// Esc) – Pendant zu MeetingDetailView.swift und MeetingSummaryTab.swift. Besitzt das
/// <see cref="MeetingDocument"/> und gibt es in <see cref="Close"/> frei (speichert Ausstehendes).
/// </summary>
public sealed class MeetingDetailView : UserControl
{
    /// <summary>Nur für UI-Snapshots: mit dem Tab „Zusammenfassung“ öffnen.</summary>
    internal static bool StartOnSummaryTab;
    /// <summary>Nur für UI-Snapshots: Zusammenfassung gleich im Bearbeiten öffnen.</summary>
    internal static bool StartEditingSummary;

    private readonly AppState _state;
    private readonly MeetingDocument _doc;
    private bool _showSummary = StartOnSummaryTab;
    private string? _actionMessage;
    private bool _actionFailed;
    private bool _updatingTitle;
    /// <summary>Im Menü gewählte Sprache für die nächste Zusammenfassung; null = Vorbelegung (<see cref="DefaultSummaryLanguage"/>).</summary>
    private AppLanguage? _chosenSummaryLanguage;
    /// <summary>Entwurf der Zusammenfassung während des Bearbeitens; null = nicht im Bearbeiten. Übersteht Tab-Wechsel.</summary>
    private string? _summaryDraft;

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

        if (StartEditingSummary && _doc.Transcript.Summary is { } initial) _summaryDraft = initial.Markdown;
        RefreshHeader();
        BuildActions();
        BuildTab();
        PreviewKeyDown += OnPreviewKeyDown;

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
    private bool IsEditingSummary => _summaryDraft != null;

    /// <summary>Sprache einer vorhandenen Zusammenfassung, sonst der Standard aus den Einstellungen.</summary>
    private AppLanguage DefaultSummaryLanguage =>
        Transcript.Summary?.Language ?? _state.Settings.SummaryLanguage.Resolved(Transcript.Language);

    private AppLanguage SummaryLanguage => _chosenSummaryLanguage ?? DefaultSummaryLanguage;

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
                Content = t.Summary == null ? "Zusammenfassen" : "Neu zusammenfassen",
                IsEnabled = !t.Confidential && !_doc.IsDeleted && !IsEditingSummary,
            }.Styled("Luna.Primary");
            Luna.SetIcon(summarize, IconKind.Sparkles);
            // Deaktivierte Knöpfe zeigen ihren Tooltip nur so.
            ToolTipService.SetShowOnDisabled(summarize, true);
            summarize.ToolTip = t.Confidential ? "Vertrauliche Meetings werden nicht an Claude geschickt."
                : IsEditingSummary ? "Erst die Bearbeitung der Zusammenfassung sichern oder abbrechen."
                : "Fasst das Meeting über die Claude CLI zusammen (Text geht an Anthropic)";
            summarize.Click += (_, _) => RequestSummary();
            left.Children.Add(summarize);
            left.Children.Add(SummaryLanguageButton());
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

        // Umbrechen statt überlappen, wenn das Fenster schmal ist (Sprachmenü macht die Zeile breiter).
        var buttons = new WrapPanel();
        foreach (var child in left.Children.Cast<FrameworkElement>().ToList())
        {
            left.Children.Remove(child);
            child.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(child);
        }
        var row = new DockPanel();
        DockPanel.SetDock(delete, Dock.Right);
        delete.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(delete);
        row.Children.Add(buttons);

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

    /// <summary>
    /// Sprache der nächsten Zusammenfassung: oben die Sprache des Meetings, darunter alle anderen alphabetisch
    /// (Pendant zu SummaryLanguageMenu).
    /// </summary>
    private Button SummaryLanguageButton()
    {
        var selected = SummaryLanguage;
        var button = new Button
        {
            Content = selected.DisplayName(), IsEnabled = !Transcript.Confidential,
            ToolTip = "Sprache der Zusammenfassung – Standard unter Einstellungen → Meetings",
        }.Styled("Luna.Secondary");
        Luna.SetIcon(button, IconKind.Globe);
        var menu = new ContextMenu();
        void Add(AppLanguage language, string header)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = language == selected };
            item.Click += (_, _) => { _chosenSummaryLanguage = language; BuildActions(); };
            menu.Items.Add(item);
        }
        var meetingLanguage = Transcript.Language;
        Add(meetingLanguage, $"{meetingLanguage.DisplayName()} (Sprache des Meetings)");
        menu.Items.Add(new Separator());
        foreach (var language in AppLanguageSorting.SortedByDisplayName.Where(l => l != meetingLanguage))
            Add(language, language.DisplayName());
        button.ContextMenu = menu;
        button.Click += (_, _) => MenuButton.Open(button);
        return button;
    }

    /// <summary>Reihenfolge der Rückfragen: erst „bearbeitete Fassung ersetzen?“, dann der einmalige Cloud-Hinweis.</summary>
    private void RequestSummary()
    {
        _summaryTab.IsChecked = true;
        if (Transcript.Summary?.IsEdited == true)
        {
            var replace = LunaDialog.Confirm(this, "Bearbeitete Zusammenfassung ersetzen?",
                "Du hast diese Zusammenfassung bearbeitet. Eine neue Zusammenfassung von Claude ersetzt deine Änderungen.",
                "Neu zusammenfassen");
            if (!replace) return;
        }
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
        _state.Summarizer.Summarize(_doc.Transcript, SummaryLanguage);
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
            stack.Children.Add(_summaryDraft is { } draft ? SummaryEditor(draft) : SummaryCard(summary));
        }
        else if (!IsSummarizing)
        {
            var content = new VStack { Spacing = 10 };
            content.Children.Add(new SectionTitle { Title = "Noch keine Zusammenfassung", Icon = IconKind.Sparkles });
            content.Children.Add(new TextBlock
            {
                Text = "„Zusammenfassen“ schickt das Transkript über die Claude CLI an Claude und legt Kurzfassung, Themen, Entscheidungen, " +
                       "Aufgaben und offene Fragen hier ab. Die Sprache wählst du daneben, Standard und Anweisung unter Einstellungen → Meetings.",
            }.Styled("Luna.Hint"));
            var adjust = new Button { Content = "Einstellungen öffnen…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 0, 0, 0) }.Styled("Luna.Quiet");
            adjust.Click += (_, _) => _state.OpenSettings(SettingsSection.Meetings);
            content.Children.Add(adjust);
            stack.Children.Add(new LunaCard { Content = content });
        }
        return stack;
    }

    private FrameworkElement SummaryCard(MeetingSummary summary)
    {
        var edit = new Button { Content = "Bearbeiten", IsEnabled = !IsSummarizing && !_doc.IsDeleted }.Styled("Luna.Quiet");
        Luna.SetIcon(edit, IconKind.Pencil);
        edit.ToolTip = "Zusammenfassung umschreiben (Strg+E) – wird im Meeting und in summary.md gespeichert";
        edit.Click += (_, _) => BeginSummaryEdit();
        var origin = new TextBlock { Text = Origin(summary), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
            .Styled("Luna.Caption");
        var head = new DockPanel();
        DockPanel.SetDock(edit, Dock.Right);
        head.Children.Add(edit);
        head.Children.Add(origin);

        var stack = new VStack { Spacing = 8 };
        stack.Children.Add(head);
        stack.Children.Add(new LunaCard { Padding = new Thickness(20), Content = new MarkdownView(summary.Markdown) });
        return stack;
    }

    /// <summary>
    /// Markdown-Quelltext der Zusammenfassung bearbeiten. Feste Schriftbreite, weil die Einrückung die Gliederung bestimmt
    /// (<see cref="MarkdownBlock"/>). Strg+S sichert, Esc bricht ab.
    /// </summary>
    private FrameworkElement SummaryEditor(string draft)
    {
        var editor = new TextBox
        {
            Text = draft, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, MinHeight = 380, FontSize = 12.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Top,
        };
        var save = new Button { Content = "Sichern", IsEnabled = draft.Trim().Length > 0, ToolTip = "Strg+S" }.Styled("Luna.Primary");
        editor.TextChanged += (_, _) =>
        {
            if (_summaryDraft == null) return;   // schon gesichert/abgebrochen
            _summaryDraft = editor.Text;
            save.IsEnabled = editor.Text.Trim().Length > 0;
        };
        save.Click += (_, _) => SaveSummaryEdit();
        var cancel = new Button { Content = "Abbrechen", ToolTip = "Esc" }.Styled("Luna.Quiet");
        cancel.Click += (_, _) => CancelSummaryEdit();
        var buttons = new HStack { Spacing = 8 };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var stack = new VStack { Spacing = 10 };
        stack.Children.Add(new SectionTitle { Title = "Zusammenfassung bearbeiten", Icon = IconKind.Pencil });
        stack.Children.Add(editor);
        stack.Children.Add(new TextBlock
        {
            Text = "Markdown: „## “ für Überschriften, „- “ für Punkte, zwei Leerzeichen davor rücken ein, „- [ ] “ für Aufgaben, **fett** für Hervorhebungen.",
        }.Styled("Luna.Hint"));
        stack.Children.Add(buttons);
        editor.Loaded += (_, _) => { editor.Focus(); editor.CaretIndex = 0; };
        return new LunaCard { Content = stack };
    }

    private void BeginSummaryEdit()
    {
        if (Transcript.Summary is not { } summary || IsSummarizing || _doc.IsDeleted) return;
        _summaryDraft = summary.Markdown;
        _summaryTab.IsChecked = true;
        BuildActions();
        BuildTab();
    }

    private void SaveSummaryEdit()
    {
        if (_summaryDraft is not { } draft || draft.Trim().Length == 0) return;
        _summaryDraft = null;
        _doc.ReviseSummary(draft);
        BuildActions();
        BuildTab();
    }

    private void CancelSummaryEdit()
    {
        if (_summaryDraft == null) return;
        _summaryDraft = null;
        BuildActions();
        BuildTab();
    }

    /// <summary>Strg+E bearbeitet, Strg+S sichert, Esc bricht das Bearbeiten ab.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.E && !IsEditingSummary && Transcript.Summary != null)
        {
            BeginSummaryEdit();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S && IsEditingSummary)
        {
            SaveSummaryEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsEditingSummary)
        {
            CancelSummaryEdit();
            e.Handled = true;
        }
    }

    /// <summary>„Erstellt 21.09.2026, 10:00 mit Claude (sonnet) · Englisch · bearbeitet 21.09.2026, 10:05“</summary>
    internal static string Origin(MeetingSummary summary)
    {
        var model = summary.Model is { Length: > 0 } m ? $" ({m})" : "";
        var parts = new List<string> { $"Erstellt {LunaDates.DateTime(summary.CreatedAt)} mit Claude{model}" };
        if (summary.Language is { } language) parts.Add(language.DisplayName());
        if (summary.EditedAt is { } edited) parts.Add($"bearbeitet {LunaDates.DateTime(edited)}");
        return string.Join(" · ", parts);
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
