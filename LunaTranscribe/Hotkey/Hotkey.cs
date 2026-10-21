using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LunaTranscribe.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

public enum HotkeySlot
{
    Primary,
    Secondary,
}

/// <summary>
/// Eine Tastenkombination: Virtual-Key-Code + Modifier. Wird als JSON in den Einstellungen gespeichert.
/// </summary>
public sealed class Hotkey : IEquatable<Hotkey>
{
    public int VirtualKey { get; set; }
    public HotkeyModifiers Modifiers { get; set; }

    public Hotkey() { }

    public Hotkey(int virtualKey, HotkeyModifiers modifiers = HotkeyModifiers.None)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
    }

    /// <summary>Haupt-Hotkey, Default F2 (ohne Modifier) – wie in der macOS-Version.</summary>
    public static Hotkey DefaultPrimary => new(0x71 /* VK_F2 */);

    public static Hotkey None => new();

    [JsonIgnore]
    public bool IsSet => VirtualKey != 0;

    /// <summary>Lesbare Darstellung, z. B. „Strg+Umschalt+F2“.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!IsSet) return "–";
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Strg");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Umschalt");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    private static string KeyName(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        switch (key)
        {
            case Key.Space: return "Leertaste";
            case Key.Return: return "Enter";
            case Key.Back: return "Rücktaste";
            case Key.Tab: return "Tab";
            case Key.Escape: return "Esc";
            case Key.Insert: return "Einfg";
            case Key.Delete: return "Entf";
            case Key.Home: return "Pos1";
            case Key.End: return "Ende";
            case Key.PageUp: return "Bild auf";
            case Key.PageDown: return "Bild ab";
            case Key.Left: return "Pfeil links";
            case Key.Right: return "Pfeil rechts";
            case Key.Up: return "Pfeil auf";
            case Key.Down: return "Pfeil ab";
            case Key.CapsLock: return "Feststelltaste";
            case Key.Scroll: return "Rollen";
            case Key.Pause: return "Pause";
            case Key.PrintScreen: return "Druck";
            case Key.NumLock: return "Num";
        }
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num " + (key - Key.NumPad0);

        // Zeichen-Tasten über das aktuelle Tastaturlayout auflösen (z. B. „Ü“, „#“).
        var ch = MapVirtualKey((uint)vk, 2 /* MAPVK_VK_TO_CHAR */) & 0xFFFF;
        if (ch >= 0x20) return char.ToUpperInvariant((char)ch).ToString();
        return key.ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public bool Equals(Hotkey? other) => other is not null && other.VirtualKey == VirtualKey && other.Modifiers == Modifiers;
    public override bool Equals(object? obj) => Equals(obj as Hotkey);
    public override int GetHashCode() => HashCode.Combine(VirtualKey, Modifiers);
    public override string ToString() => DisplayName;
}
