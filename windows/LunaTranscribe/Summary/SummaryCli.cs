using System;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;

namespace LunaTranscribe.Summary;

/// <summary>
/// Eine lokal installierte Anbieter-CLI, die eine Anweisung und einen Text bekommt und nur Text zurückgibt – ohne
/// Werkzeuge, ohne Projektkontext. Neue Anbieter: <see cref="SummaryProvider"/>-Fall in LunaCore, eine Implementierung hier
/// und ein Fall in <see cref="SummaryProviderUiExtensions.LocateCli"/>. Einstellungen und <see cref="MeetingSummarizer"/>
/// brauchen sonst keine Änderung.
///
/// Achtung Local-First: Die CLI läuft lokal, das Modell aber beim Anbieter. Der Text verlässt den PC (über das Konto des
/// Nutzers). Die Oberfläche weist darauf hin.
/// </summary>
public interface ISummaryCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    CliProcess Process { get; }

    /// <summary>Schickt <paramref name="input"/> mit der Anweisung <paramref name="instructions"/> an das Modell und gibt die Antwort zurück.</summary>
    /// <param name="model">Modellname der CLI; null/"" = ihr Standard.</param>
    Task<string> CompleteAsync(string instructions, string input, string? model, TimeSpan timeout, CancellationToken ct);

    SummaryProvider Provider => Process.Provider;
    string Executable => Process.Executable;
    Task<string> VersionAsync(CancellationToken ct = default) => Process.VersionAsync(ct);
}

public static class SummaryProviderUiExtensions
{
    /// <summary>
    /// Findet die CLI dieses Anbieters (<paramref name="customPath"/> leer = automatisch). Liest dabei Dateien und die
    /// Registry – nicht auf dem UI-Thread aufrufen, wenn es sich vermeiden lässt.
    /// </summary>
    public static ISummaryCli? LocateCli(this SummaryProvider provider, string? customPath)
    {
        if (CliProcess.Locate(provider, customPath) is not { } process) return null;
        return provider switch
        {
            SummaryProvider.Antigravity => new AntigravityCli(process),
            SummaryProvider.Codex => new CodexCli(process),
            _ => new ClaudeCli(process),
        };
    }

    /// <summary>Einmaliger Hinweis vor der ersten Zusammenfassung mit diesem Anbieter.</summary>
    public static string CloudNotice(this SummaryProvider p)
    {
        var text = $"Die Transkription bleibt lokal. Für die Zusammenfassung schickt LunaTranscribe den Text des Meetings über die {p.CliName()} an {p.Vendor()} – mit deinem {p.Account()}.";
        if (p.DataUse() is { } dataUse) text += " " + dataUse;
        return text + " Dieser Hinweis erscheint je Anbieter nur einmal.";
    }

    /// <summary>Was der Anbieter laut eigener Doku mit dem Text macht, wenn das vom Üblichen abweicht.</summary>
    public static string? DataUse(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Google nutzt Eingaben der Antigravity CLI standardmäßig zur Produktverbesserung; abschalten kannst du das in den Antigravity-Einstellungen. Außerdem behält agy jede Zusammenfassung samt Transkript in seinem eigenen Verlauf auf diesem PC (im Benutzerordner unter .gemini\\antigravity-cli).",
        SummaryProvider.Codex => "Ob OpenAI den Text zum Training nutzt, richtet sich nach den Datenkontrollen deines ChatGPT-Kontos.",
        _ => null,
    };

    /// <summary>Hinweis unter dem Pfadfeld in den Einstellungen.</summary>
    public static string PathHint(this SummaryProvider p)
    {
        var hint = $"Leer = automatisch suchen. Installieren: {p.InstallCommand()}, danach einmal „{p.LoginCommand()}“ im Terminal starten und anmelden.";
        if (p == SummaryProvider.Claude) hint += " Gilt auch für die Nachbearbeitung mit Claude Haiku.";
        return hint;
    }

    /// <summary>Hinweis unter dem Modellfeld in den Einstellungen.</summary>
    public static string ModelHint(this SummaryProvider p) => p switch
    {
        SummaryProvider.Antigravity => "Name aus „agy models“, z. B. „gemini-3.8-flash-medium“ (schneller); leer = Standard der CLI.",
        SummaryProvider.Codex => "Modellname wie in Codex unter /model; leer = Standard deines ChatGPT-Kontos.",
        _ => "Alias wie „sonnet“ oder „opus“; leer = Standard der CLI.",
    };
}
