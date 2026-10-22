using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.UI.Pages.Settings;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>Eigenes Wörterbuch: gelernte Vorschläge oben, Einträge darunter.</summary>
public sealed class DictionaryPage : UserControl
{
    public DictionaryPage(AppState state)
    {
        Content = new LunaPage
        {
            Title = "Wörterbuch",
            Subtitle = "Namen und Fachbegriffe, die immer richtig geschrieben werden sollen.",
            Content = new DictionaryContent(state),
        };
    }
}

internal sealed class DictionaryContent : SettingsSectionView
{
    private string _query = "";

    public DictionaryContent(AppState state) : base(state) { }

    protected override bool Affects(object? sender, string? property) =>
        sender == State.Settings && property is nameof(LunaTranscribe.Settings.SettingsStore.VocabularyEnabled)
            or nameof(LunaTranscribe.Settings.SettingsStore.VocabularyEntries) or nameof(LunaTranscribe.Settings.SettingsStore.VocabularySuggestions)
            or nameof(LunaTranscribe.Settings.SettingsStore.Engine);

    protected override void Build(VStack root)
    {
        var s = State.Settings;
        var toggle = new CheckBox { IsChecked = s.VocabularyEnabled }.Styled("Luna.Switch");
        toggle.Click += (_, _) => { s.VocabularyEnabled = toggle.IsChecked == true; };
        var row = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(new TextBlock { Text = "Eigenes Wörterbuch verwenden (Diktat und Meeting)", FontSize = 13.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }
            .Styled("Luna.Text"));
        root.Children.Add(new LunaCard { Content = row });

        if (!s.VocabularyEnabled) return;
        var suggestions = s.VocabularySuggestions;
        if (suggestions.Count > 0) root.Children.Add(SuggestionsCard(suggestions));
        root.Children.Add(EntriesCard());
    }

    // MARK: Vorschläge

    private LunaCard SuggestionsCard(System.Collections.Generic.List<VocabularySuggestion> suggestions)
    {
        var stack = new VStack { Spacing = 10 };
        var header = new DockPanel();
        var open = new Chip { Text = $"{suggestions.Count} offen" }.Brushed(Chip.ForegroundProperty, "Luna.Honey");
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        header.Children.Add(new SectionTitle { Title = "Gelernt aus deinen Korrekturen", Icon = IconKind.Sparkles });
        stack.Children.Add(header);
        stack.Children.Add(Hint("Wenn du im Verlauf oder in einem Meeting ein Wort korrigierst, schlägt LunaTranscribe hier einen Eintrag vor. Aktiv wird er erst, wenn du ihn übernimmst."));

        foreach (var suggestion in suggestions.OrderByDescending(x => x.Count).ThenByDescending(x => x.LastSeen))
        {
            var line = new DockPanel { LastChildFill = false };
            var variant = new TextBlock { Text = suggestion.Variant, VerticalAlignment = VerticalAlignment.Center, TextDecorations = TextDecorations.Strikethrough }
                .Styled("Luna.TextMuted");
            variant.TextWrapping = TextWrapping.NoWrap;
            var arrow = new LunaIcon { Kind = IconKind.ChevronRight, Size = 12, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }
                .Brushed(LunaIcon.ForegroundProperty, "Luna.Lavender");
            var term = new TextBox { Text = suggestion.Term, Width = 200, VerticalAlignment = VerticalAlignment.Center };
            var accept = new Button { Content = "Übernehmen", Margin = new Thickness(10, 0, 0, 0) }.Styled("Luna.Primary");
            var ignore = new Button { Content = "Ignorieren" }.Styled("Luna.Quiet");
            term.TextChanged += (_, _) => accept.IsEnabled = term.Text.Trim().Length > 0;
            accept.Click += (_, _) => State.AcceptSuggestion(suggestion with { Term = term.Text.Trim() });
            ignore.Click += (_, _) => State.IgnoreSuggestion(suggestion);
            foreach (var e in new FrameworkElement[] { variant, arrow, term }) { DockPanel.SetDock(e, Dock.Left); line.Children.Add(e); }
            if (suggestion.Count > 1)
            {
                var count = new Chip { Text = $"{suggestion.Count}×", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                    .Brushed(Chip.ForegroundProperty, "Luna.Honey");
                DockPanel.SetDock(count, Dock.Left);
                line.Children.Add(count);
            }
            DockPanel.SetDock(ignore, Dock.Right);
            DockPanel.SetDock(accept, Dock.Right);
            line.Children.Add(ignore);
            line.Children.Add(accept);
            stack.Children.Add(line);
        }
        return new LunaCard { Content = stack };
    }

    // MARK: Einträge

    private LunaCard EntriesCard()
    {
        var stack = new VStack { Spacing = 10 };
        var header = new DockPanel();
        var status = new TextBlock { Text = State.VocabularyStatus, VerticalAlignment = VerticalAlignment.Center }.Styled("Luna.Caption");
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        header.Children.Add(new SectionTitle { Title = "Einträge", Icon = IconKind.Book });
        stack.Children.Add(header);

        var filter = new TextBox { Text = _query };
        Luna.SetPlaceholder(filter, "Filtern");
        var list = new VStack { Spacing = 8 };
        filter.TextChanged += (_, _) => { _query = filter.Text; FillEntries(list); };
        stack.Children.Add(filter);

        var columns = new DockPanel();
        var c1 = new TextBlock { Text = "Richtige Schreibweise", Width = 208 }.Styled("Luna.SmallCaps");
        DockPanel.SetDock(c1, Dock.Left);
        columns.Children.Add(c1);
        columns.Children.Add(new TextBlock { Text = "Wird erkannt als … (durch Kommas getrennt)" }.Styled("Luna.SmallCaps"));
        stack.Children.Add(columns);

        FillEntries(list);
        stack.Children.Add(list);

        var add = new Button { Content = "Begriff hinzufügen", HorizontalAlignment = HorizontalAlignment.Left };
        Luna.SetIcon(add, IconKind.Plus);
        add.Click += (_, _) =>
        {
            _query = "";
            var entries = State.Settings.VocabularyEntries;
            entries.Add(new VocabularyEntry(""));
            State.Settings.VocabularyEntries = entries;   // baut neu auf, neue Zeile bekommt den Fokus
            Dispatcher.BeginInvoke(() => FocusLastTerm(), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        stack.Children.Add(add);
        stack.Children.Add(new LearnMore
        {
            Text = "Varianten werden immer ersetzt – unabhängig von Engine und Sprache, nur an Wortgrenzen und unabhängig von " +
                   "Groß-/Kleinschreibung; auch der Begriff selbst in falscher Schreibweise wird korrigiert. Das gilt für Diktat und " +
                   "Meetings. Nach der Nachbearbeitung wird das Wörterbuch erneut angewendet, damit das Sprachmodell nichts zurückkorrigiert. " +
                   "Das akustische Boosting der macOS-Version (Zusatzmodell, das am Audio prüft, ob ein Begriff gesagt wurde) gibt es unter Windows noch nicht – trage typische Fehlschreibungen als Varianten ein.",
        });
        return new LunaCard { Content = stack };
    }

    private TextBox? _lastTerm;

    private void FocusLastTerm() => _lastTerm?.Focus();

    private void FillEntries(VStack list)
    {
        list.Children.Clear();
        var entries = State.Settings.VocabularyEntries;
        var needle = _query.Trim().ToLowerInvariant();
        foreach (var entry in entries)
        {
            var matches = needle.Length == 0 || entry.Term.Length == 0 || entry.Term.ToLowerInvariant().Contains(needle)
                          || entry.Aliases.Any(a => a.ToLowerInvariant().Contains(needle));
            if (!matches) continue;
            list.Children.Add(EntryRow(entry.Id));
        }
        if (entries.Count == 0) list.Children.Add(Hint("Noch keine Begriffe. Beispiel: „weclapp“ – erkannt als „Weglap, Weg Lab“."));
    }

    /// <summary>Eine Zeile: Begriff und Fehlerkennungen als kommagetrennter Text; jede Änderung wird sofort gespeichert.</summary>
    private FrameworkElement EntryRow(Guid id)
    {
        var entry = State.Settings.VocabularyEntries.First(e => e.Id == id);
        var row = new DockPanel();
        var term = new TextBox { Text = entry.Term, Width = 200, Margin = new Thickness(0, 0, 8, 0) };
        Luna.SetPlaceholder(term, "Richtige Schreibweise");
        var aliases = new TextBox { Text = string.Join(", ", entry.Aliases) };
        Luna.SetPlaceholder(aliases, "Falsch erkannt als …");
        var remove = new Button { ToolTip = "Begriff entfernen", Margin = new Thickness(6, 0, 0, 0) }.Styled("Luna.Quiet").Brushed(System.Windows.Controls.Control.ForegroundProperty, "Luna.Rose");
        Luna.SetIcon(remove, IconKind.Minus);

        void Save() => Quietly(() =>
        {
            var all = State.Settings.VocabularyEntries;
            var target = all.FirstOrDefault(e => e.Id == id);
            if (target == null) return;
            target.Term = term.Text.Trim();
            target.Aliases = aliases.Text.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            State.Settings.VocabularyEntries = all;
        });
        term.TextChanged += (_, _) => Save();
        aliases.TextChanged += (_, _) => Save();
        remove.Click += (_, _) =>
        {
            var all = State.Settings.VocabularyEntries;
            all.RemoveAll(e => e.Id == id);
            State.Settings.VocabularyEntries = all;
        };
        _lastTerm = term;

        DockPanel.SetDock(term, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(term);
        row.Children.Add(remove);
        row.Children.Add(aliases);
        return row;
    }
}
