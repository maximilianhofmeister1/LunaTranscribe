using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using LunaCore;
using LunaTranscribe.Output;
using LunaTranscribe.Settings;
using LunaTranscribe.Summary;

namespace LunaTranscribe.Meeting;

/// <summary>
/// Ein im Hauptfenster geöffnetes Meeting. Änderungen (Titel, Sprecher, Text) werden 0,5 s verzögert automatisch
/// gespeichert; eine fertige Zusammenfassung wird übernommen, ohne ungespeicherte Änderungen zu verlieren.
/// Korrigierte Beiträge speisen das lernende Wörterbuch.
///
/// Claude kann dasselbe Meeting über luna-mcp umbenennen oder löschen. Ohne eigene Änderungen übernimmt das
/// Dokument den neuen Stand sofort; mit ungespeicherten Änderungen führt <see cref="MeetingStore.Save"/> beide
/// Seiten zusammen (was der Nutzer geändert hat, gewinnt). Ein gelöschtes Meeting wird nie neu angelegt.
/// Läuft auf dem UI-Thread; <see cref="Dispose"/> speichert Ausstehendes und meldet sich ab.
/// </summary>
public sealed class MeetingDocument : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Der Inhalt wurde von außen ersetzt (Zusammenführung, Claude, Zusammenfassung) – Ansicht neu aufbauen.</summary>
    public event Action? Replaced;

    private readonly MeetingStore _store;
    private readonly MeetingSummarizer _summarizer;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _saveTimer;
    private MeetingTranscript _transcript;
    /// <summary>Stand der Platte beim Laden bzw. letzten Speichern – Basis für die Zusammenführung.</summary>
    private MeetingTranscript _base;
    private string? _saveError;
    private int _learnedSuggestions;
    private bool _isDeleted;
    private bool _disposed;

    public MeetingDocument(MeetingTranscript transcript, MeetingStore store, MeetingSummarizer summarizer, SettingsStore settings, Dispatcher dispatcher)
    {
        _transcript = transcript.Clone();
        _base = transcript.Clone();
        _store = store;
        _summarizer = summarizer;
        _settings = settings;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _summarizer.Completed += OnSummaryCompleted;
        _store.ExternalChanges += OnExternalChanges;
        _store.Register(this);
    }

    /// <summary>Aktueller Stand (nur lesen – ändern über die Methoden, damit gespeichert wird).</summary>
    public MeetingTranscript Transcript => _transcript;

    public Guid Id => _transcript.Id;

    public string? SaveError
    {
        get => _saveError;
        private set { if (_saveError == value) return; _saveError = value; OnPropertyChanged(); }
    }

    /// <summary>Zahl der Wörterbuch-Vorschläge aus der letzten Korrektur (für einen kurzen Hinweis).</summary>
    public int LearnedSuggestions
    {
        get => _learnedSuggestions;
        private set { _learnedSuggestions = value; OnPropertyChanged(); }
    }

    /// <summary>Das Meeting ist nicht mehr in der Bibliothek (von Claude oder im Explorer gelöscht).</summary>
    public bool IsDeleted
    {
        get => _isDeleted;
        private set { if (_isDeleted == value) return; _isDeleted = value; OnPropertyChanged(); }
    }

    public bool HasPendingChanges => _saveTimer.IsEnabled;

    // MARK: Ändern

    public void SetTitle(string title)
    {
        if (_transcript.Title == title) return;
        _transcript.Title = title;
        Changed();
    }

    /// <summary>Wie in Swift: auch ein leerer Name wird gespeichert (zeigt dann wieder die Sprecher-ID).</summary>
    public void SetSpeakerName(string speakerID, string name)
    {
        if (_transcript.SpeakerNames.TryGetValue(speakerID, out var current) && current == name) return;
        _transcript.SpeakerNames[speakerID] = name;
        Changed();
    }

    /// <summary>Im JSON optional (ältere Meetings haben das Feld nicht); „aus“ wird als fehlendes Feld gespeichert.</summary>
    public void SetConfidential(bool confidential)
    {
        bool? value = confidential ? true : null;
        if (_transcript.IsConfidential == value) return;
        _transcript.IsConfidential = value;
        Changed();
    }

    /// <summary>Ersetzt den Text eines Beitrags und lernt aus der Korrektur.</summary>
    public void CorrectTurn(Guid turnId, string newText)
    {
        var index = _transcript.Turns.FindIndex(t => t.Id == turnId);
        if (index < 0) return;
        var old = _transcript.Turns[index].Text;
        var cleaned = newText.Trim();
        if (cleaned == old || cleaned.Length == 0) return;
        _transcript.Turns[index] = _transcript.Turns[index] with { Text = cleaned };
        Changed();

        var (state, changed) = VocabularyLearner.Learn(_settings.VocabularyState, old, cleaned);
        if (changed > 0)
        {
            _settings.VocabularyState = state;
            Log.Write($"learned {changed} vocabulary suggestion(s) from a meeting correction");
        }
        LearnedSuggestions = changed;
    }

    /// <summary>
    /// Übernimmt den vom Nutzer bearbeiteten Text der Zusammenfassung und speichert sofort – das Bearbeiten ist ein
    /// bewusster Schritt mit „Sichern“, keine laufende Eingabe. Leerer Text wird ignoriert (Löschen ist keine
    /// Bearbeitung); unveränderter Text markiert nichts als bearbeitet.
    /// </summary>
    public void ReviseSummary(string markdown)
    {
        if (_transcript.Summary is not { } summary || string.IsNullOrWhiteSpace(markdown)) return;
        var revised = summary.Revised(markdown);
        if (ReferenceEquals(revised, summary)) return;
        _transcript.Summary = revised;
        Changed();
        Flush();
    }

    /// <summary>Sofort speichern (z. B. beim Schließen, Wechseln oder vor dem Zusammenfassen).</summary>
    public void Flush()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        Save();
    }

    // MARK: Speichern

    private void Changed()
    {
        OnPropertyChanged(nameof(Transcript));
        if (IsDeleted) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        if (IsDeleted) return;
        try
        {
            var saved = _store.Save(_transcript.Clone(), _base);
            _base = saved.Clone();
            if (!saved.Equals(_transcript)) Apply(saved);
            SaveError = null;
        }
        catch (MeetingNotFoundException)
        {
            MarkDeleted();
        }
        catch (Exception ex)
        {
            SaveError = "Speichern fehlgeschlagen: " + ex.Message;
            Log.Write("meeting save failed: " + ex.Message);
        }
    }

    private void OnSummaryCompleted(Guid id, MeetingSummary summary)
    {
        if (id != Id || _disposed) return;
        _transcript.Summary = summary;
        Changed();
        Replaced?.Invoke();
    }

    /// <summary>
    /// Claude hat das Meeting geändert oder gelöscht. Mit ausstehenden eigenen Änderungen übernimmt das nächste
    /// Speichern (Zusammenführung) den neuen Stand; sonst sofort neu laden.
    /// </summary>
    private void OnExternalChanges(IReadOnlySet<Guid> ids)
    {
        if (!ids.Contains(Id) || _saveTimer.IsEnabled || _disposed) return;
        try
        {
            var fresh = _store.Load(Id);
            _base = fresh.Clone();
            Apply(fresh);
        }
        catch (Exception)
        {
            MarkDeleted();
        }
    }

    private void MarkDeleted()
    {
        _saveTimer.Stop();
        IsDeleted = true;
        SaveError = "Dieses Meeting ist nicht mehr in der Bibliothek – es wurde gelöscht (Papierkorb) oder im Explorer verschoben.";
    }

    /// <summary>Übernimmt einen gespeicherten Stand, ohne erneut zu speichern.</summary>
    private void Apply(MeetingTranscript stored)
    {
        _transcript = stored.Clone();
        OnPropertyChanged(nameof(Transcript));
        Replaced?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        _summarizer.Completed -= OnSummaryCompleted;
        _store.ExternalChanges -= OnExternalChanges;
        _store.Unregister(this);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
