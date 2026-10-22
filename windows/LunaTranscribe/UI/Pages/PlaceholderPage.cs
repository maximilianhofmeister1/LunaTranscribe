using System.Windows.Controls;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages;

/// <summary>Vorläufige Seite für Bereiche, die noch nicht umgesetzt sind.</summary>
public sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(string title)
    {
        var hint = new TextBlock { Text = "Dieser Bereich folgt in Kürze." }.Styled("Luna.Hint");
        Content = new LunaPage { Title = title, Content = hint };
    }
}
