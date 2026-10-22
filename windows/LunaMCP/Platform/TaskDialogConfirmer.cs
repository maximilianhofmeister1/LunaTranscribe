using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LunaMCP.Server;

namespace LunaMCP.Platform;

/// <summary>
/// Bestätigung als nativer Windows-Dialog (TaskDialog), den luna-mcp selbst zeigt – außerhalb der Reichweite des
/// Modells: Kein Tool-Aufruf und kein Transkript-Text kann ihn bestätigen. „Abbrechen“ ist die Standardtaste,
/// der Dialog liegt über allen Fenstern, nach <see cref="Timeout"/> ohne Antwort gilt die Anfrage als abgelehnt, und
/// ohne interaktive Sitzung (z. B. per SSH) wird nichts geändert. Läuft auf einem eigenen STA-Thread, damit der
/// Server weiter antworten kann.
/// </summary>
public sealed class TaskDialogConfirmer : IUserConfirmer
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public Task<ConfirmationOutcome> ConfirmAsync(ConfirmationRequest request, string client)
    {
        // Ohne sichtbare Fensterstation (Dienst, SSH-Sitzung) gibt es niemanden, der zustimmen könnte.
        if (!Environment.UserInteractive)
            return Task.FromResult(ConfirmationOutcome.Unavailable("Es ist keine interaktive Windows-Sitzung aktiv (z. B. Zugriff per SSH)."));

        var completion = new TaskCompletionSource<ConfirmationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Show(request, client));
            }
            catch (Exception ex)
            {
                McpLog.Write("Bestätigungsdialog fehlgeschlagen: " + ex.Message);
                completion.SetResult(ConfirmationOutcome.Unavailable("Der Bestätigungsdialog ließ sich nicht öffnen."));
            }
        })
        {
            IsBackground = true,
            Name = "luna-mcp Bestätigung",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private const int CancelButtonId = 101;
    private const int ConfirmButtonId = 102;

    private ConfirmationOutcome Show(ConfirmationRequest request, string client)
    {
        McpLog.Write($"Bestätigungsdialog: {request.Kind}");
        try
        {
            return ShowTaskDialog(request, client);
        }
        catch (EntryPointNotFoundException)
        {
            // Ohne Common Controls 6 (sollte über das Manifest nie passieren): einfacher Meldungsdialog.
            return ShowMessageBox(request, client);
        }
    }

    private ConfirmationOutcome ShowTaskDialog(ConfirmationRequest request, string client)
    {
        var timedOut = false;
        var timeoutMs = (long)Timeout.TotalMilliseconds;
        var strings = new List<IntPtr>();
        IntPtr Str(string s)
        {
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        TaskDialogCallback callback = (hwnd, msg, wParam, _, _) =>
        {
            switch (msg)
            {
                case TDN_CREATED:
                    // Über allen Fenstern, damit der Dialog nicht hinter Claude verschwindet.
                    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    SetForegroundWindow(hwnd);
                    break;
                case TDN_TIMER:
                    if (wParam.ToInt64() >= timeoutMs && !timedOut)
                    {
                        timedOut = true;
                        SendMessage(hwnd, TDM_CLICK_BUTTON, (IntPtr)CancelButtonId, IntPtr.Zero);
                    }
                    break;
            }
            return 0;
        };

        var buttons = Marshal.AllocHGlobal(Marshal.SizeOf<TASKDIALOG_BUTTON>() * 2);
        try
        {
            // Erste Taste = Standard (Enter). Bewusst „Abbrechen“, damit ein versehentliches Enter nichts ändert.
            Marshal.StructureToPtr(new TASKDIALOG_BUTTON { nButtonID = CancelButtonId, pszButtonText = Str("Abbrechen") }, buttons, false);
            Marshal.StructureToPtr(new TASKDIALOG_BUTTON { nButtonID = ConfirmButtonId, pszButtonText = Str(request.ConfirmButton) },
                                   buttons + Marshal.SizeOf<TASKDIALOG_BUTTON>(), false);

            var config = new TASKDIALOGCONFIG
            {
                cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_CALLBACK_TIMER,
                pszWindowTitle = Str("LunaTranscribe"),
                hMainIcon = request.IsDestructive ? TD_ERROR_ICON : TD_WARNING_ICON,
                pszMainInstruction = Str(request.Title),
                pszContent = Str(request.Message(client)),
                cButtons = 2,
                pButtons = buttons,
                nDefaultButton = CancelButtonId,
                pszFooter = Str($"Ohne Antwort wird nach {(int)Timeout.TotalSeconds} Sekunden abgebrochen. Diese Anfrage stellt luna-mcp, nicht LunaTranscribe selbst."),
                hFooterIcon = TD_INFORMATION_ICON,
                pfCallback = Marshal.GetFunctionPointerForDelegate(callback),
            };
            var hr = TaskDialogIndirect(ref config, out var button, out _, out _);
            GC.KeepAlive(callback);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (timedOut) return ConfirmationOutcome.TimedOut;
            return button == ConfirmButtonId ? ConfirmationOutcome.Approved : ConfirmationOutcome.Declined;
        }
        finally
        {
            Marshal.FreeHGlobal(buttons);
            foreach (var p in strings) Marshal.FreeHGlobal(p);
        }
    }

    private ConfirmationOutcome ShowMessageBox(ConfirmationRequest request, string client)
    {
        var text = request.Message(client) + $"\n\nOK = „{request.ConfirmButton}“, Abbrechen = nichts ändern.";
        var result = MessageBoxTimeoutW(IntPtr.Zero, text, "LunaTranscribe – " + request.Title,
            MB_OKCANCEL | MB_DEFBUTTON2 | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND, 0, (uint)Timeout.TotalMilliseconds);
        return result switch
        {
            IDOK => ConfirmationOutcome.Approved,
            MB_TIMEDOUT => ConfirmationOutcome.TimedOut,
            _ => ConfirmationOutcome.Declined,
        };
    }

    // MARK: Win32

    private const uint TDN_CREATED = 0;
    private const uint TDN_TIMER = 4;
    private const uint TDM_CLICK_BUTTON = 0x0400 + 102;
    private const int TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    private const int TDF_CALLBACK_TIMER = 0x0800;
    private static readonly IntPtr TD_WARNING_ICON = new(0xFFFF);
    private static readonly IntPtr TD_ERROR_ICON = new(0xFFFE);
    private static readonly IntPtr TD_INFORMATION_ICON = new(0xFFFD);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;
    private const int IDOK = 1;
    private const int MB_TIMEDOUT = 32000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TaskDialogCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr refData);

    // commctrl.h packt beide Strukturen mit 1 Byte (pshpack1.h).
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        public IntPtr pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public int dwFlags;
        public int dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint cxWidth;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG pTaskConfig, out int pnButton, out int pnRadioButton, out int pfVerificationFlagChecked);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxTimeoutW(IntPtr hWnd, string text, string caption, uint type, ushort languageId, uint milliseconds);
}
