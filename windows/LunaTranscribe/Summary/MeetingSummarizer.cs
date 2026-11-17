using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LunaCore;
using LunaTranscribe.Meeting;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;

namespace LunaTranscribe.Summary;

/// <summary>
/// Erstellt Meeting-Zusammenfassungen über die eingestellte CLI (<see cref="SummaryProvider"/>: Claude, Antigravity oder
/// Codex). Läuft je Meeting höchstens einmal gleichzeitig und
/// schreibt das Ergebnis selbst in die Bibliothek – auch wenn das Meeting inzwischen nicht mehr angezeigt wird.
/// Läuft auf dem UI-Thread.
/// </summary>
public sealed class MeetingSummarizer : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Meldet eine fertige Zusammenfassung, damit ein geöffnetes Meeting sie übernimmt.</summary>
    public event Action<Guid, MeetingSummary>? Completed;

    private readonly SettingsStore _settings;
    private readonly MeetingStore _store;
    private readonly Dictionary<Guid, CancellationTokenSource> _tasks = new();
    private readonly Dictionary<Guid, SummaryProvider> _providers = new();
    private readonly Dictionary<Guid, string> _errors = new();

    public MeetingSummarizer(SettingsStore settings, MeetingStore store)
    {
        _settings = settings;
        _store = store;
    }

    /// <summary>Meetings, für die gerade eine Zusammenfassung läuft.</summary>
    public IReadOnlyCollection<Guid> Running => _tasks.Keys.ToList();

    public bool IsRunning(Guid id) => _tasks.ContainsKey(id);

    /// <summary>Anbieter des laufenden Laufs für dieses Meeting; null = keine Zusammenfassung läuft.</summary>
    public SummaryProvider? RunningProvider(Guid id) => _tasks.ContainsKey(id) && _providers.TryGetValue(id, out var p) ? p : null;

    /// <summary>Letzter Fehler je Meeting (bis zum nächsten Versuch).</summary>
    public string? Error(Guid id) => _errors.TryGetValue(id, out var e) ? e : null;

    /// <summary>
    /// Fasst <paramref name="transcript"/> in <paramref name="language"/> zusammen (Wahl im Meeting, vorbelegt aus
    /// <see cref="SettingsStore.SummaryLanguage"/>). Ersetzt eine vorhandene – auch bearbeitete – Zusammenfassung; das
    /// Nachfragen ist Sache der Oberfläche.
    /// </summary>
    public void Summarize(MeetingTranscript transcript, AppLanguage language)
    {
        var id = transcript.Id;
        if (_tasks.ContainsKey(id)) return;
        _errors.Remove(id);
        // Vertraulich heißt: kein Text in die Cloud – weder über luna-mcp noch über die Zusammenfassung.
        if (transcript.Confidential)
        {
            SetError(id, "Dieses Meeting ist als vertraulich markiert und wird nicht in der Cloud zusammengefasst.");
            return;
        }
        var provider = _settings.SummaryProvider;
        var customPath = _settings.CliPath(provider);
        var instructions = SummaryPrompt.Instructions(_settings.SummaryPrompt, language);
        var input = transcript.Render(MeetingTranscript.ExportFormat.Text);
        var model = _settings.Model(provider);

        var cts = new CancellationTokenSource();
        _tasks[id] = cts;
        _providers[id] = provider;
        Changed();
        _ = RunAsync(id, provider, customPath, instructions, input, model.Length == 0 ? null : model, language, transcript.WordCount, cts);
    }

    private async Task RunAsync(Guid id, SummaryProvider provider, string customPath, string instructions, string input, string? model,
        AppLanguage language, int wordCount, CancellationTokenSource cts)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Locate liest Dateien und die Registry – nicht auf dem UI-Thread.
            var cli = await Task.Run(() => provider.LocateCli(customPath), cts.Token) ?? throw CliException.NotFound(provider);
            Log.Write($"summary started: {wordCount} words in {language.Code()} via {cli.Executable}");
            var markdown = await cli.CompleteAsync(instructions, input, model, ISummaryCli.DefaultTimeout, cts.Token);
            if (!IsCurrent(id, cts)) return;
            _tasks.Remove(id);
            Finish(id, new MeetingSummary(markdown, DateTime.UtcNow, model, language, provider: provider));
            Log.Write($"summary done in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (IsCurrent(id, cts)) _tasks.Remove(id);
        }
        catch (Exception ex)
        {
            if (!IsCurrent(id, cts)) return;
            _tasks.Remove(id);
            _errors[id] = ex.Message;
            Log.Write("summary failed: " + ex.Message);
        }
        finally
        {
            Changed();
        }
    }

    private bool IsCurrent(Guid id, CancellationTokenSource cts) => _tasks.TryGetValue(id, out var current) && current == cts;

    public void Cancel(Guid id)
    {
        if (!_tasks.Remove(id, out var cts)) return;
        cts.Cancel();
        Changed();
    }

    /// <summary>Prüft die CLI eines Anbieters für die Einstellungen: Pfad und Version – oder wirft mit Meldung.</summary>
    public async Task<string> CheckCliAsync(SummaryProvider provider)
    {
        var customPath = _settings.CliPath(provider);
        var cli = await Task.Run(() => provider.LocateCli(customPath)) ?? throw CliException.NotFound(provider);
        var version = await cli.VersionAsync();
        return $"{cli.Executable} · {version}";
    }

    /// <summary>Frischen Stand von der Platte lesen, Zusammenfassung setzen, speichern, geöffnete Ansicht informieren.</summary>
    private void Finish(Guid id, MeetingSummary summary)
    {
        try
        {
            var latest = _store.Load(id);
            latest.Summary = summary;
            _store.Update(latest);
        }
        catch (MeetingNotFoundException)
        {
            // Inzwischen gelöscht (z. B. von Claude über luna-mcp) – das Ergebnis hat kein Ziel mehr.
            Log.Write("summary discarded: meeting no longer exists");
        }
        catch (Exception ex)
        {
            _errors[id] = "Zusammenfassung erstellt, aber nicht gespeichert: " + ex.Message;
        }
        Completed?.Invoke(id, summary);
    }

    private void SetError(Guid id, string message)
    {
        _errors[id] = message;
        Changed();
    }

    private void Changed([CallerMemberName] string? _ = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Running)));
    }
}
