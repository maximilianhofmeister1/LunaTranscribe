using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LunaTranscribe.Core;

/// <summary>
/// Markiert einen Entwickler-Selbsttest, der über ein Kommandozeilen-Flag startet (z. B. „--refine-selftest“).
/// Signatur: <c>static Task&lt;int&gt; Name(string[] args)</c> – args ohne das Flag selbst, Rückgabe = Exit-Code.
/// Läuft auf dem UI-Thread (Dispatcher vorhanden), ohne Tray, ohne Hotkey-Hook und ohne vorgeladene Modelle.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SelfTestAttribute : Attribute
{
    public string Flag { get; }
    public SelfTestAttribute(string flag) => Flag = flag;
}

/// <summary>
/// Findet und startet Selbsttests (Pendant zu den „--…-selftest“-Flags der macOS-Version). Ausgaben gehen
/// auf die Konsole des aufrufenden Terminals (die App ist eine WinExe, deshalb AttachConsole) und ins Log.
/// </summary>
public static class SelfTests
{
    private static MethodInfo? Find(string flag) =>
        typeof(SelfTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .FirstOrDefault(m => m.GetCustomAttribute<SelfTestAttribute>()?.Flag == flag);

    public static bool IsRequested(string[] args) => args.Length > 0 && args[0].StartsWith("--") && Find(args[0]) != null;

    public static async Task<int> RunAsync(string[] args)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        var method = Find(args[0])!;
        try
        {
            var task = (Task<int>)method.Invoke(null, new object[] { args.Skip(1).ToArray() })!;
            return await task;
        }
        catch (Exception ex)
        {
            Print("Selbsttest abgebrochen: " + (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex));
            return 1;
        }
    }

    /// <summary>Ausgabe für Selbsttests: Konsole + Log.</summary>
    public static void Print(string line)
    {
        Console.WriteLine(line);
        Output.Log.Write("[selftest] " + line);
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
