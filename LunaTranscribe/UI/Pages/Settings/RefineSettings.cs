using System.Windows.Controls;
using LunaTranscribe.Core;
using LunaTranscribe.Refine;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

public sealed class RefineSettings : SettingsSectionView
{
    public RefineSettings(AppState state) : base(state) { }

    protected override void Subscribe() => State.Refiners.PropertyChanged += OnExternalChange;
    protected override void Unsubscribe() => State.Refiners.PropertyChanged -= OnExternalChange;

    protected override void Build(VStack root)
    {
        var s = State.Settings;
        var models = new VStack { Spacing = 10 };
        foreach (var kind in new[] { RefinerKind.Lfm2_1_2B, RefinerKind.Qwen3_1_7B, RefinerKind.Qwen3_4B })
        {
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = kind.Label() }.Styled("Luna.Text"));
            label.Children.Add(new TextBlock { Text = kind.Description(), FontSize = 11.5 }.Styled("Luna.Caption"));
            var rb = new RadioButton { Content = label, GroupName = "Refiner", IsChecked = State.RefinerKind == kind };
            rb.Checked += (_, _) => s.RefinerKey = kind.Key();
            models.Children.Add(rb);
        }
        var status = new LoadStatusView { Status = State.Refiners.ToLoadStatus(), Retry = () => State.Refiners.Retry(State.RefinerKind) };

        root.Children.Add(Card("Sprachmodell", IconKind.Sparkles,
            models,
            status,
            new LearnMore
            {
                Text = "Der dritte Hotkey lässt ein lokales Sprachmodell das Diktat bereinigen: Rechtschreibung, Grammatik, Füllwörter, " +
                       "Wiederholungen und Versprecher – Wortwahl und Ton bleiben erhalten. Meeting-Transkripte sind nicht betroffen. " +
                       "Schlägt die Nachbearbeitung fehl (Modell lädt noch, Fehler, mehr als 20 s, unplausible Antwort), wird der Rohtext eingefügt. " +
                       "Die Modelle laufen über llama.cpp auf der CPU; die Dauer hängt stark vom Prozessor ab. Ist ein Hotkey gesetzt, lädt das Modell " +
                       "beim Start vor, sonst bei der ersten Nachbearbeitung. Apple Intelligence aus der macOS-Version gibt es unter Windows nicht.",
            }));

        root.Children.Add(Card("Anweisung an das Sprachmodell", IconKind.TextLines,
            PromptEditor(string.IsNullOrWhiteSpace(s.RefinePromptOverride) ? RefinePrompt.DefaultTemplate : s.RefinePromptOverride,
                RefinePrompt.IsDefault(s.RefinePromptOverride),
                text => s.RefinePromptOverride = RefinePrompt.IsDefault(text) ? "" : text,
                () => s.RefinePromptOverride = ""),
            Hint("Englisch funktioniert bei kleinen Modellen am zuverlässigsten. „{language}“ wird durch die Diktatsprache ersetzt; ein leeres Feld nutzt den Standard. Beispielpaare (Deutsch/Englisch) hängt die App automatisch an.")));
    }
}
