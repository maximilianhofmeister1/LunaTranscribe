using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Meeting;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Components;

/// <summary>
/// Meetings in der Hintergrund-Verarbeitung: Fortschritt, Wartende, Fehlgeschlagene mit „Erneut versuchen“ /
/// „Verwerfen“, gerettete (unterbrochene) Aufnahmen mit „Transkription starten“. Genutzt im Popover (kompakt) und auf
/// der Seite Meetings. Abonniert die Warteschlange selbst,
/// solange sie angezeigt wird; Fortschritt aktualisiert nur Balken und Schritt (kein Neuaufbau).
/// </summary>
public sealed class MeetingQueueView : UserControl
{
    private readonly MeetingProcessingQueue _queue;
    private readonly bool _compact;
    private readonly VStack _rows;
    private readonly Dictionary<Guid, (ProgressBar Bar, TextBlock Step)> _progress = new();
    private string _shape = "";

    public MeetingQueueView(MeetingProcessingQueue queue, bool compact = false)
    {
        _queue = queue;
        _compact = compact;
        _rows = new VStack { Spacing = compact ? 8 : 10 };
        Content = _rows;
        Loaded += (_, _) =>
        {
            _queue.PropertyChanged += OnQueueChanged;
            _queue.ProgressChanged += OnProgress;
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            _queue.PropertyChanged -= OnQueueChanged;
            _queue.ProgressChanged -= OnProgress;
        };
        Rebuild();
    }

    private void OnQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MeetingProcessingQueue.Items)) Dispatcher.BeginInvoke(Rebuild);
    }

    private void OnProgress(QueueItem item)
    {
        if (!_progress.TryGetValue(item.Id, out var p)) return;
        p.Bar.Value = item.Fraction;
        p.Step.Text = item.Step;
    }

    private void Rebuild()
    {
        var items = _queue.Items;
        // Nur neu aufbauen, wenn sich Aufträge oder ihre Phase ändern – sonst flackern Knöpfe und Ladekreis.
        var shape = string.Join("|", items.Select(i => $"{i.Id}:{i.Phase}:{i.Failure}"));
        if (shape == _shape && _rows.Children.Count > 0)
        {
            foreach (var i in items) OnProgress(i);
            return;
        }
        _shape = shape;
        _rows.Children.Clear();
        _progress.Clear();
        foreach (var item in items) _rows.Children.Add(Row(item));
    }

    private FrameworkElement Row(QueueItem item)
    {
        var stack = new VStack { Spacing = 4 };

        var head = new DockPanel();
        FrameworkElement icon = item.Phase switch
        {
            QueueItemPhase.Processing => new Spinner { Width = 12, Height = 12 },
            QueueItemPhase.Waiting => new LunaIcon { Kind = IconKind.Hourglass, Size = 12 }.Brushed(LunaIcon.ForegroundProperty, "Luna.InkMuted"),
            QueueItemPhase.Interrupted => new LunaIcon { Kind = IconKind.Undo, Size = 12 }.Brushed(LunaIcon.ForegroundProperty, "Luna.Honey"),
            _ => new LunaIcon { Kind = IconKind.Warning, Size = 12 }.Brushed(LunaIcon.ForegroundProperty, "Luna.Rose"),
        };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var clock = new TextBlock { Text = MeetingTranscript.Clock(item.Job.DurationSeconds), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) }
            .Styled("Luna.Caption");
        clock.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(clock, Dock.Right);
        head.Children.Add(clock);
        head.Children.Add(new TextBlock
        {
            Text = item.Job.Title, FontSize = _compact ? 12 : 13, FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        }.Styled("Luna.TextBase"));
        stack.Children.Add(head);

        switch (item.Phase)
        {
            case QueueItemPhase.Processing:
            {
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = item.Fraction, Margin = new Thickness(0, 2, 0, 0) };
                stack.Children.Add(bar);
                var step = Caption(item.Step);
                _progress[item.Id] = (bar, step);
                stack.Children.Add(Footer(step, Small("Abbrechen", "Luna.QuietSmall", () => ConfirmDiscard(item))));
                break;
            }
            case QueueItemPhase.Waiting:
                stack.Children.Add(Footer(Caption("Wartet auf Verarbeitung"), Small("Verwerfen", "Luna.QuietSmall", () => ConfirmDiscard(item))));
                break;
            case QueueItemPhase.Interrupted:
            {
                var message = Caption("Unterbrochene Aufnahme – LunaTranscribe wurde während des Meetings beendet. Bis dahin ist alles gesichert.");
                message.TextWrapping = TextWrapping.Wrap;
                message.MaxHeight = _compact ? 48 : 64;
                stack.Children.Add(message);
                var buttons = new HStack { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
                buttons.Children.Add(Small("Transkription starten", "Luna.SecondarySmall", () => _queue.Retry(item.Id)));
                buttons.Children.Add(Small("Verwerfen", "Luna.QuietSmall", () => ConfirmDiscard(item)));
                stack.Children.Add(buttons);
                break;
            }
            default:
            {
                var message = Caption(item.Failure ?? "Unbekannter Fehler");
                message.Brushed(TextBlock.ForegroundProperty, "Luna.Rose");
                message.TextWrapping = TextWrapping.Wrap;
                message.TextTrimming = TextTrimming.CharacterEllipsis;
                message.MaxHeight = _compact ? 32 : 64;
                stack.Children.Add(message);
                var buttons = new HStack { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
                buttons.Children.Add(Small("Erneut versuchen", "Luna.SecondarySmall", () => _queue.Retry(item.Id)));
                buttons.Children.Add(Small("Verwerfen", "Luna.QuietSmall", () => ConfirmDiscard(item)));
                stack.Children.Add(buttons);
                break;
            }
        }
        return stack;
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Styled("Luna.Caption");
        t.TextWrapping = TextWrapping.NoWrap;
        return t;
    }

    private static DockPanel Footer(TextBlock text, Button button)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        button.Margin = new Thickness(6, -3, -4, -3);
        dock.Children.Add(button);
        dock.Children.Add(text);
        return dock;
    }

    private static Button Small(string text, string style, Action click)
    {
        var b = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center }.Styled(style);
        b.Click += (_, _) => click();
        return b;
    }

    private void ConfirmDiscard(QueueItem item)
    {
        var ok = LunaDialog.Confirm(this, "Aufnahme verwerfen?",
            $"„{item.Job.Title}“ ({MeetingTranscript.Clock(item.Job.DurationSeconds)}) wird endgültig gelöscht und nicht transkribiert.",
            "Verwerfen");
        if (ok) _queue.Discard(item.Id);
    }
}
