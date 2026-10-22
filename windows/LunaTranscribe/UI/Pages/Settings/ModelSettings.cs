using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LunaCore;
using LunaTranscribe.Core;
using LunaTranscribe.Engine;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

public sealed class ModelSettings : SettingsSectionView
{
    public ModelSettings(AppState state) : base(state) { }

    protected override void Build(VStack root)
    {
        var s = State.Settings;

        var engines = new VStack { Spacing = 8 };
        foreach (var kind in new[] { EngineKind.Parakeet, EngineKind.Cohere })
        {
            var rb = new RadioButton { Content = kind.Label(), GroupName = "Engine", IsChecked = s.Engine == kind };
            rb.Checked += (_, _) =>
            {
                s.Engine = kind;
                // Sprachen ggf. auf etwas Unterstütztes zurücksetzen
                if (!s.PrimaryLanguage.IsSupported(kind)) s.PrimaryLanguage = AppLanguage.English;
                if (!s.SecondaryLanguage.IsSupported(kind)) s.SecondaryLanguage = AppLanguage.English;
            };
            engines.Children.Add(rb);
        }
        var status = new LoadStatusView { Status = State.Models.ToLoadStatus(), Retry = () => State.Models.Retry(s.Engine) };

        root.Children.Add(Card("Spracherkennung", IconKind.Waveform,
            engines,
            status,
            Row("Modellordner", ModelPaths.Base, Button("Im Explorer zeigen", () =>
            {
                Directory.CreateDirectory(ModelPaths.Base);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ModelPaths.Base + "\"") { UseShellExecute = true });
            })),
            new LearnMore
            {
                Text = "Modelle (Transkription, Sprecher, Nachbearbeitung) werden einmalig von Hugging Face geladen; danach arbeitet " +
                       "die App komplett offline. Parakeet unterstützt 25 europäische Sprachen und erkennt die Sprache selbst, " +
                       "Cohere 14 Sprachen inklusive Chinesisch, Japanisch, Koreanisch und Arabisch. Beide laufen auf der CPU (ONNX Runtime).",
            }));

        // Windows-Ergänzung: Modell nach Inaktivität entladen.
        int[] minutes = { 0, 2, 5, 10, 30, 60 };
        root.Children.Add(Card("Arbeitsspeicher", IconKind.Cpu,
            Row("Modell nach Inaktivität entladen", "Spart ~750 MB RAM. Beim nächsten Diktat startet die Aufnahme sofort, das Modell lädt parallel (wenige Sekunden).",
                Picker(minutes, Array.IndexOf(minutes, s.UnloadAfterMinutes) >= 0 ? s.UnloadAfterMinutes : 10,
                    m => m == 0 ? "Nie" : m == 60 ? "nach 1 Stunde" : $"nach {m} Minuten", m => s.UnloadAfterMinutes = m, width: 170))));
    }
}
