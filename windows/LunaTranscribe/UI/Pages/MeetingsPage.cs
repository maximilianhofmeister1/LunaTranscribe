using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LunaTranscribe.Core;
using LunaTranscribe.Meeting;
using LunaTranscribe.UI.Components;
using LunaTranscribe.UI.MainWindow;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>
/// Meeting-Bibliothek: Liste (Suche, Verarbeitung, nach Monat gruppiert) links, gewähltes Meeting rechts
/// (Pendant zu MeetingsPage.swift). Die Auswahl lebt im <see cref="MainRouter"/>, damit Mitteilungen und Übersicht
/// ein Meeting direkt öffnen können.
/// </summary>
public sealed class MeetingsPage : UserControl
{
    private static readonly CultureInfo German = new("de-DE");

    private readonly AppState _state;
    private readonly TextBox _search;
    private readonly Border _queueBox;
    private readonly TextBlock _queueTitle;
    private readonly ContentControl _issue = new() { Focusable = false };
    private readonly StackPanel _list = new();
    private readonly ContentControl _detailHost = new() { Focusable = false };
    private MeetingDetailView? _detail;

    private MeetingStore Store => _state.Meetings;
    private MainRouter Router => _state.Router;

    public MeetingsPage(AppState state)
    {
        _state = state;

        // Liste
        var title = new TextBlock { Text = "Meetings", FontSize = 26, Margin = new Thickness(0, 8, 0, 0) }.Styled("Luna.Display");

        _search = new TextBox().Styled("Luna.PlainTextBox");
        Luna.SetPlaceholder(_search, "Suchen in Titeln und Texten");
        _search.TextChanged += (_, _) => RebuildList();
        var searchRow = new DockPanel();
        var searchIcon = new LunaIcon { Kind = IconKind.Search, Size = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) }
            .Brushed(LunaIcon.ForegroundProperty, "Luna.InkMuted");
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(_search);
        var searchBox = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Child = searchRow,
        }.Brushed(Border.BackgroundProperty, "Luna.Surface").Brushed(Border.BorderBrushProperty, "Luna.Stroke");

        var queueStack = new VStack { Spacing = 8 };
        _queueTitle = SmallCaps("In Verarbeitung");
        queueStack.Children.Add(_queueTitle);
        queueStack.Children.Add(new MeetingQueueView(state.MeetingQueue, compact: true));
        _queueBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = queueStack }
            .Brushed(Border.BackgroundProperty, "Luna.Surface");

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = _list, Margin = new Thickness(-4, 0, -4, 0) };

        var openFolder = new Button { Content = "Meeting-Ordner öffnen" }.Styled("Luna.Quiet");
        Luna.SetIcon(openFolder, IconKind.Folder);
        openFolder.Click += (_, _) => Store.OpenLibraryFolder();
        var reload = new Button { ToolTip = "Neu einlesen (z. B. nach Änderungen im Explorer)" }.Styled("Luna.Quiet");
        Luna.SetIcon(reload, IconKind.Refresh);
        reload.Click += (_, _) => Store.Reload();
        var footer = new DockPanel { Margin = new Thickness(-6, 0, -6, 0) };
        DockPanel.SetDock(reload, Dock.Right);
        footer.Children.Add(reload);
        footer.Children.Add(openFolder);
        openFolder.HorizontalAlignment = HorizontalAlignment.Left;

        var listGrid = new Grid { Margin = new Thickness(14, 28, 14, 10) };
        for (var i = 0; i < 6; i++) listGrid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        AddRow(listGrid, title, 0, 12);
        AddRow(listGrid, searchBox, 1, 12);
        AddRow(listGrid, _queueBox, 2, 12);
        AddRow(listGrid, _issue, 3, 0);
        AddRow(listGrid, scroll, 4, 6);
        AddRow(listGrid, footer, 5, 0);
        var listColumn = new Border { Child = listGrid }.Brushed(Border.BackgroundProperty, "Luna.Background");

        var divider = new Border { Width = 1 }.Brushed(Border.BackgroundProperty, "Luna.Stroke");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(listColumn);
        Grid.SetColumn(divider, 1);
        root.Children.Add(divider);
        Grid.SetColumn(_detailHost, 2);
        root.Children.Add(_detailHost);
        root.SetResourceReference(BackgroundProperty, "Luna.Background");
        Content = root;

        Loaded += (_, _) =>
        {
            Store.PropertyChanged += OnStoreChanged;
            Router.PropertyChanged += OnRouterChanged;
            _state.MeetingQueue.PropertyChanged += OnQueueChanged;
            _state.Summarizer.PropertyChanged += OnSummarizerChanged;
            SelectFirstIfNeeded();
            RefreshQueue();
            RefreshIssue();
            RebuildList();
            ShowDetail();
        };
        Unloaded += (_, _) =>
        {
            Store.PropertyChanged -= OnStoreChanged;
            Router.PropertyChanged -= OnRouterChanged;
            _state.MeetingQueue.PropertyChanged -= OnQueueChanged;
            _state.Summarizer.PropertyChanged -= OnSummarizerChanged;
            CloseDetail();
        };
    }

    private static void AddRow(Grid grid, FrameworkElement element, int row, double bottom)
    {
        element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, element.Margin.Right, bottom);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    // MARK: Änderungen

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.PropertyName == nameof(MeetingStore.FolderIssue)) { RefreshIssue(); return; }
        SelectFirstIfNeeded();
        RebuildList();
        ShowDetail();
    });

    private void OnRouterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainRouter.SelectedMeetingId)) return;
        RebuildList();
        ShowDetail();
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingProcessingQueue.Items)) Dispatcher.BeginInvoke(RefreshQueue);
    }

    private void OnSummarizerChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RebuildList);

    private void RefreshQueue()
    {
        _queueBox.Visibility = _state.MeetingQueue.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _queueTitle.Text = (_state.MeetingQueue.PendingCount > 0 ? "In Verarbeitung" : "Nicht verarbeitet").ToUpper(German);
    }

    private void RefreshIssue()
    {
        if (Store.FolderIssue is not { } issue) { _issue.Content = null; _issue.Margin = new Thickness(0); return; }
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new LunaIcon { Kind = IconKind.Drive, Size = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 7, 0) }
            .Brushed(LunaIcon.ForegroundProperty, "Luna.Honey"));
        var text = new TextBlock { Text = issue, FontSize = 11.5 }.Styled("Luna.Caption").Brushed(TextBlock.ForegroundProperty, "Luna.Honey");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        _issue.Content = grid;
    }

    private void SelectFirstIfNeeded()
    {
        if (Router.SelectedMeetingId is { } id && Store.Entry(id) != null) return;
        Router.SelectedMeetingId = Store.Entries.FirstOrDefault()?.Id;
    }

    // MARK: Liste

    private void RebuildList()
    {
        _list.Children.Clear();
        var entries = Store.Search(_search.Text);
        string? month = null;
        foreach (var entry in entries)
        {
            var m = LunaDates.Month(entry.Date);
            if (m != month)
            {
                month = m;
                var header = SmallCaps(m);
                header.Margin = new Thickness(10, _list.Children.Count == 0 ? 2 : 12, 0, 4);
                _list.Children.Add(header);
            }
            _list.Children.Add(Row(entry));
        }
        if (entries.Count == 0 && Store.Entries.Count > 0)
            _list.Children.Add(new TextBlock { Text = "Keine Treffer.", Margin = new Thickness(10, 6, 0, 0) }.Styled("Luna.Hint"));
    }

    private FrameworkElement Row(MeetingEntry entry)
    {
        var selected = Router.SelectedMeetingId == entry.Id;
        var border = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4),
            Child = new MeetingRow(entry, _state.Summarizer.IsRunning(entry.Id)), Background = Brushes.Transparent,
        };
        if (selected) border.SetResourceReference(Border.BackgroundProperty, "Luna.Surface");
        else
        {
            border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "Luna.SurfaceHover");
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        }
        border.OnClick(() => Router.SelectedMeetingId = entry.Id);
        return border;
    }

    private static TextBlock SmallCaps(string text)
    {
        var t = new TextBlock { Text = text.ToUpper(German), FontSize = 11, FontWeight = FontWeights.SemiBold }.Styled("Luna.TextBase")
            .Brushed(TextBlock.ForegroundProperty, "Luna.InkMuted");
        return t;
    }

    // MARK: Detail

    private void ShowDetail()
    {
        var id = Router.SelectedMeetingId;
        if (id is { } selected && Store.Entry(selected) != null)
        {
            if (_detail?.MeetingId == selected) return;   // schon offen – Bearbeitung nicht unterbrechen
            CloseDetail();
            try
            {
                var transcript = Store.Load(selected);
                var document = new MeetingDocument(transcript, Store, _state.Summarizer, _state.Settings, Dispatcher);
                _detail = new MeetingDetailView(_state, document);
                _detailHost.Content = _detail;
            }
            catch (Exception ex)
            {
                _detailHost.Content = ErrorView(ex.Message);
            }
            return;
        }
        CloseDetail();
        _detailHost.Content = EmptyState();
    }

    private void CloseDetail()
    {
        _detail?.Close();
        _detail = null;
    }

    private FrameworkElement EmptyState()
    {
        var stack = new VStack { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
        stack.Children.Add(new LunaIcon { Kind = IconKind.MoonStars, Size = 52, HorizontalAlignment = HorizontalAlignment.Center }
            .Brushed(LunaIcon.ForegroundProperty, "Luna.Lavender"));
        stack.Children.Add(new TextBlock
        {
            Text = Store.Entries.Count == 0 ? "Noch keine Meetings" : "Wähle ein Meeting", FontSize = 22, TextAlignment = TextAlignment.Center,
        }.Styled("Luna.Display"));
        stack.Children.Add(new TextBlock
        {
            Text = "Meetings startest du über das Symbol im Infobereich. Nach dem Beenden erkennt LunaTranscribe die Sprecher, " +
                   "transkribiert lokal und legt alles hier ab.",
            TextAlignment = TextAlignment.Center,
        }.Styled("Luna.TextMuted"));
        return new Border { Child = stack }.Brushed(Border.BackgroundProperty, "Luna.Background");
    }

    private static FrameworkElement ErrorView(string message)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 40, 28, 28), VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(new LunaIcon { Kind = IconKind.Warning, Size = 14, Margin = new Thickness(0, 1, 8, 0) }.Brushed(LunaIcon.ForegroundProperty, "Luna.Rose"));
        row.Children.Add(new TextBlock { Text = message, MaxWidth = 520 }.Styled("Luna.Text").Brushed(TextBlock.ForegroundProperty, "Luna.Rose"));
        return new Border { Child = row }.Brushed(Border.BackgroundProperty, "Luna.Background");
    }
}
