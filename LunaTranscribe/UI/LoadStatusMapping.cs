using LunaCore;
using LunaTranscribe.Engine;
using LunaTranscribe.UI.Theme;

namespace LunaTranscribe.UI;

/// <summary>
/// Übersetzt die Zustände der Modell-Manager in die einheitliche <see cref="LoadStatus"/>-Darstellung,
/// damit Popover, Übersicht, Seitenleiste und Einstellungen sie gleich anzeigen.
/// </summary>
public static class LoadStatusMapping
{
    public static LoadStatus ToLoadStatus(this ModelManager models)
    {
        var s = models.State;
        return s.Kind switch
        {
            ModelStateKind.Idle => new LoadStatus(LoadPhase.Idle, models.StatusText),
            ModelStateKind.Unloaded => new LoadStatus(LoadPhase.Idle, $"{s.Engine?.ShortLabel()} pausiert – lädt beim nächsten Diktat"),
            ModelStateKind.Loading => new LoadStatus(LoadPhase.Loading, models.StatusText,
                s.Progress < 0.9 && !models.FilesPresent ? s.Progress / 0.9 : null),
            ModelStateKind.Ready => new LoadStatus(LoadPhase.Ready, $"{s.Engine?.ShortLabel()} bereit"),
            ModelStateKind.Failed => new LoadStatus(LoadPhase.Failed, s.Error ?? "Fehler"),
            _ => new LoadStatus(LoadPhase.Idle, models.StatusText),
        };
    }
}
