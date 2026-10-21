using LunaTranscribe.Core;
using LunaTranscribe.Output;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI.Pages.Settings;

public sealed class GeneralSettings : SettingsSectionView
{
    public GeneralSettings(AppState state) : base(state) { }

    protected override void Build(VStack root)
    {
        var s = State.Settings;
        root.Children.Add(Card("Start & Klang", IconKind.Power,
            ToggleRow("Beim Anmelden automatisch starten",
                "Funktioniert zuverlässig, wenn die App aus dem Installationsordner läuft (scripts\\build-release.ps1).",
                s.LaunchAtLogin, v => { s.LaunchAtLogin = v; Rebuild(); }),
            s.LaunchAtLoginError is { } error ? Error(error) : null,
            ToggleRow("Töne bei Start und Stopp", null, s.PlaySounds, v => s.PlaySounds = v),
            Row("Autostart-Apps", "Erscheint in den Windows-Einstellungen unter Apps → Autostart.",
                Button("Windows-Einstellungen öffnen", Permissions.OpenStartupSettings))));
    }
}
