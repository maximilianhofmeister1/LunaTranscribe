using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using LunaTranscribe.Hotkeys;

namespace LunaTranscribe.Output;

/// <summary>
/// Fügt Text in die gerade aktive App ein: Zwischenablage setzen → Strg+V simulieren → Zwischenablage wiederherstellen.
/// Muss auf dem UI-Thread (STA) laufen.
/// </summary>
public static class TextInjector
{
    private static readonly string[] SafeFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html,
        DataFormats.Xaml, DataFormats.CommaSeparatedValue, DataFormats.FileDrop, DataFormats.Bitmap,
    };

    public static async Task InsertAsync(string text)
    {
        // Vorherigen Inhalt sichern (nur gängige Formate; alles andere bleibt unangetastet).
        var previous = Snapshot();

        if (!TrySetClipboardText(text))
        {
            Log.Write("clipboard busy – paste skipped");
            return;
        }

        // Wenn der Hotkey z. B. Alt oder Umschalt enthält und der Nutzer sie noch hält,
        // würde aus Strg+V ein Strg+Alt+V. Kurz auf das Loslassen warten.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000 && ModifierHeld()) await Task.Delay(15);

        SendCtrlV();

        // Nach kurzer Zeit alten Inhalt zurücklegen, damit der Nutzer nichts verliert.
        await Task.Delay(400);
        try
        {
            if (!Clipboard.ContainsText() || Clipboard.GetText() != text) return;   // Nutzer hat inzwischen selbst kopiert
            if (previous.Count == 0) { Clipboard.Clear(); return; }
            var obj = new DataObject();
            foreach (var (fmt, data) in previous) obj.SetData(fmt, data);
            Clipboard.SetDataObject(obj, true);
        }
        catch (Exception ex)
        {
            Log.Write("clipboard restore failed: " + ex.Message);
        }
    }

    private static bool ModifierHeld() =>
        KeyboardHook.IsDown(KeyboardHook.VK_MENU) || KeyboardHook.IsDown(KeyboardHook.VK_SHIFT)
        || KeyboardHook.IsDown(KeyboardHook.VK_LWIN) || KeyboardHook.IsDown(KeyboardHook.VK_RWIN);

    private static List<(string, object)> Snapshot()
    {
        var list = new List<(string, object)>();
        try
        {
            var obj = Clipboard.GetDataObject();
            if (obj == null) return list;
            foreach (var fmt in SafeFormats)
            {
                try
                {
                    if (!obj.GetDataPresent(fmt, false)) continue;
                    var data = obj.GetData(fmt, false);
                    if (data != null) list.Add((fmt, data));
                }
                catch { /* Format nicht lesbar – überspringen */ }
            }
        }
        catch { }
        return list;
    }

    private static bool TrySetClipboardText(string text)
    {
        // Die Zwischenablage kann kurz von einer anderen App gesperrt sein → einige Versuche.
        for (var i = 0; i < 8; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, false);
                return true;
            }
            catch (COMException)
            {
                System.Threading.Thread.Sleep(25);
            }
        }
        return false;
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // MARK: Win32

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
