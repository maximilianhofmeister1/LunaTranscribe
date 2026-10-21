using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LunaTranscribe.Hotkeys;

/// <summary>
/// Globaler Low-Level-Tastatur-Hook (WH_KEYBOARD_LL). Im Gegensatz zu RegisterHotKey liefert er
/// auch das Loslassen der Taste (nötig für „Gedrückt halten“) und kann die Hotkey-Taste
/// verschlucken, damit z. B. F2 nicht zusätzlich im aktiven Programm ankommt (Excel: Zelle bearbeiten).
/// Escape wird nur beobachtet, nie verschluckt.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action<HotkeySlot>? HotkeyDown;
    public event Action<HotkeySlot>? HotkeyUp;
    public event Action? EscapeDown;

    public Hotkey Primary { get; set; } = Hotkey.DefaultPrimary;
    public Hotkey Secondary { get; set; } = Hotkey.None;

    /// <summary>Solange true, werden keine Hotkeys ausgewertet (Hotkey-Recorder in den Einstellungen hat den Fokus).</summary>
    public volatile bool Suspended;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelKeyboardProc _proc;   // Referenz halten, sonst räumt der GC den Delegate weg
    private IntPtr _hook;
    private readonly bool[] _slotDown = new bool[2];

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Tastatur-Hook konnte nicht installiert werden (Win32-Fehler " + Marshal.GetLastWin32Error() + ").");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || Suspended) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        // Eigene synthetische Eingaben (Ctrl+V beim Einfügen) ignorieren.
        if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = (int)wParam;
        var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        var vk = (int)info.vkCode;

        if (vk == VK_ESCAPE && isDown)
        {
            var h = EscapeDown;
            if (h != null) _dispatcher.BeginInvoke(h);
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (Match(HotkeySlot.Primary, Primary, vk, isDown, isUp) || Match(HotkeySlot.Secondary, Secondary, vk, isDown, isUp))
            return (IntPtr)1;   // Taste verschlucken

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Liefert true, wenn das Ereignis zum Hotkey gehört und nicht weitergereicht werden soll.</summary>
    private bool Match(HotkeySlot slot, Hotkey hotkey, int vk, bool isDown, bool isUp)
    {
        if (!hotkey.IsSet || vk != hotkey.VirtualKey) return false;
        var i = (int)slot;

        if (isDown)
        {
            if (_slotDown[i]) return true;   // Auto-Repeat während des Haltens: schlucken, nicht erneut auslösen
            if (CurrentModifiers() != hotkey.Modifiers) return false;
            _slotDown[i] = true;
            var h = HotkeyDown;
            if (h != null) _dispatcher.BeginInvoke(h, slot);
            return true;
        }
        if (isUp && _slotDown[i])
        {
            // Loslassen zählt auch, wenn die Modifier inzwischen schon losgelassen wurden.
            _slotDown[i] = false;
            var h = HotkeyUp;
            if (h != null) _dispatcher.BeginInvoke(h, slot);
            return true;
        }
        return false;
    }

    public static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (IsDown(VK_CONTROL)) m |= HotkeyModifiers.Ctrl;
        if (IsDown(VK_MENU)) m |= HotkeyModifiers.Alt;
        if (IsDown(VK_SHIFT)) m |= HotkeyModifiers.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= HotkeyModifiers.Win;
        return m;
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    // MARK: Win32

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
