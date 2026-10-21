using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace LunaTranscribe.Audio;

public sealed record AudioInputDevice(string Uid, string Name);

/// <summary>Liest die Eingabegeräte über WASAPI/MMDevice aus.</summary>
public static class AudioDevices
{
    public static List<AudioInputDevice> InputDevices()
    {
        var list = new List<AudioInputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add(new AudioInputDevice(d.ID, d.FriendlyName));
            }
        }
        catch { /* keine Audio-Hardware / WASAPI nicht verfügbar */ }
        return list;
    }

    public static AudioInputDevice? DefaultInputDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
            using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return new AudioInputDevice(d.ID, d.FriendlyName);
        }
        catch { return null; }
    }

    /// <summary>MMDevice zur UID; null = nicht (mehr) vorhanden. Der Aufrufer besitzt das Objekt.</summary>
    public static MMDevice? Device(string uid)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var d = enumerator.GetDevice(uid);
            if (d.State != DeviceState.Active) { d.Dispose(); return null; }
            return d;
        }
        catch { return null; }
    }

    public static MMDevice? DefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch { return null; }
    }
}
