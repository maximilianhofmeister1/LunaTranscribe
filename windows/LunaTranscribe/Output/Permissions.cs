using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace LunaTranscribe.Output;

/// <summary>
/// Windows kennt für Desktop-Apps keine Bedienungshilfen-Berechtigung (SendInput funktioniert immer,
/// außer das Zielprogramm läuft als Administrator). Relevant ist nur der Mikrofon-Datenschutzschalter.
/// </summary>
public static class Permissions
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// Prüft Einstellungen → Datenschutz → Mikrofon: „Mikrofonzugriff“ und „Desktop-Apps den Zugriff erlauben“.
    /// Unbekannte Werte gelten als erlaubt – die verbindliche Prüfung ist der tatsächliche Aufnahmestart.
    /// </summary>
    public static bool MicrophoneAllowed
    {
        get
        {
            try
            {
                using var user = Registry.CurrentUser.OpenSubKey(ConsentStore);
                if (user?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return false;
                using var desktop = Registry.CurrentUser.OpenSubKey(ConsentStore + @"\NonPackaged");
                if (desktop?.GetValue("Value") is string d && d.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return false;
                // Gerätweite Sperre (Admin / Gruppenrichtlinie)
                using var machine = Registry.LocalMachine.OpenSubKey(ConsentStore);
                if (machine?.GetValue("Value") is string m && m.Equals("Deny", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            return true;
        }
    }

    public static void OpenMicrophoneSettings() => OpenSettings("ms-settings:privacy-microphone");

    public static void OpenStartupSettings() => OpenSettings("ms-settings:startupapps");

    private static void OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("open settings failed: " + ex.Message);
        }
    }
}
