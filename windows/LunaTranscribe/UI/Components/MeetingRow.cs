using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Meeting;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Components;

/// <summary>Deutsche Datumsanzeige unabhängig von der Systemsprache (wie Date.lunaDateTime in Swift).</summary>
public static class LunaDates
{
    private static readonly CultureInfo German = new("de-DE");

    /// <summary>„21. Okt. 2026, 14:03“</summary>
    public static string DateTime(DateTime date) => Local(date).ToString("d. MMM yyyy, HH:mm", German);

    /// <summary>„21. Oktober 2026 um 14:03“</summary>
    public static string LongDateTime(DateTime date) => Local(date).ToString("d. MMMM yyyy 'um' HH:mm", German);

    /// <summary>„Oktober 2026“ – Monatsgruppen der Meeting-Liste.</summary>
    public static string Month(DateTime date) => Local(date).ToString("MMMM yyyy", German);

    private static DateTime Local(DateTime date) => JsonCoding.ToLocal(date);
}

/// <summary>Eine Zeile in Meeting-Listen (Bibliothek, Übersicht): Titel, Datum · Dauer · Sprecher, Chips.</summary>
public sealed class MeetingRow : UserControl
{
    public MeetingEntry Entry { get; }

    public MeetingRow(MeetingEntry entry, bool isSummarizing = false)
    {
        Entry = entry;
        var stack = new VStack { Spacing = 4 };
        var title = new TextBlock
        {
            Text = entry.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 40,
        }.Styled("Luna.Text");
        stack.Children.Add(title);

        var meta = new TextBlock
        {
            Text = $"{LunaDates.DateTime(entry.Date)}  ·  {MeetingTranscript.Clock(entry.DurationSeconds)}  ·  {entry.SpeakerCount} Sprecher",
            FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        }.Styled("Luna.Caption");
        stack.Children.Add(meta);

        var chips = new WrapPanel();
        if (isSummarizing) chips.Children.Add(Chip("Fasst zusammen…", IconKind.Cloud, "Luna.Lavender"));
        else if (entry.HasSummary) chips.Children.Add(Chip("Zusammenfassung", IconKind.Sparkles, "Luna.Lavender"));
        if (entry.IsConfidential) chips.Children.Add(Chip("Vertraulich", IconKind.Lock, "Luna.InkMuted"));
        if (entry.IsUnsaved) chips.Children.Add(Chip("Nur in der App gesichert", IconKind.Warning, "Luna.Honey"));
        if (chips.Children.Count > 0)
        {
            chips.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(chips);
        }
        Content = stack;
    }

    public static Chip Chip(string text, IconKind icon, string tint)
    {
        var chip = new Chip { Text = text, Icon = icon, Margin = new Thickness(0, 0, 6, 4) };
        chip.SetResourceReference(ForegroundProperty, tint);
        return chip;
    }
}
