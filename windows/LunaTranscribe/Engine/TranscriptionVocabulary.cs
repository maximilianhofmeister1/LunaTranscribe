using LunaCore;

namespace LunaTranscribe.Engine;

/// <summary>
/// Das aktive eigene Wörterbuch für alle Engines – so profitieren Diktat, Meetings und Selbsttests ohne
/// Änderungen an den Aufrufern (wie configureVocabulary auf macOS). Stufe 1 (Varianten ersetzen) läuft bei
/// jeder Engine; akustisches Boosting gibt es unter Windows (noch) nicht.
/// </summary>
public static class TranscriptionVocabulary
{
    private static volatile CustomVocabulary _current = CustomVocabulary.Disabled;

    public static CustomVocabulary Current
    {
        get => _current;
        set => _current = value ?? CustomVocabulary.Disabled;
    }

    public static string Apply(string text) => _current.IsEmpty ? text : _current.Correct(text);
}
