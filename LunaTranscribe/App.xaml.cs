using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LunaTranscribe.Core;
using LunaTranscribe.Output;
using LunaTranscribe.UI;

namespace LunaTranscribe;

/// <summary>
/// Einstieg. Kein Hauptfenster – die App lebt ausschließlich im Infobereich (Tray).
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppState? _state;
    private TrayIcon? _tray;
    private OverlayWindow? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Entwickler-Selbsttests (--…-selftest): ohne Tray, ohne Hook, beenden den Prozess selbst.
        if (SelfTests.IsRequested(e.Args))
        {
            DispatcherUnhandledException += OnUnhandledException;
            Dispatcher.BeginInvoke(async () => Shutdown(await SelfTests.RunAsync(e.Args)));
            return;
        }

        // Nur eine Instanz: der globale Tastatur-Hook darf nicht doppelt laufen.
        _singleInstance = new Mutex(true, @"Local\LunaTranscribe.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("LunaTranscribe läuft bereits – siehe Infobereich der Taskleiste.", "LunaTranscribe",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("fatal: " + args.ExceptionObject);
        Log.Write("start v" + typeof(App).Assembly.GetName().Version);

        try
        {
            _state = new AppState(Dispatcher);
        }
        catch (Exception ex)
        {
            Log.Write("startup failed: " + ex);
            MessageBox.Show("LunaTranscribe konnte nicht starten:\n\n" + ex.Message, "LunaTranscribe",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _overlay = new OverlayWindow(_state);
        _tray = new TrayIcon(_state, QuitFromTray);
    }

    private void QuitFromTray() => Shutdown();

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("unhandled: " + e.Exception);
        e.Handled = true;   // Tray-App soll nicht wegen eines UI-Fehlers sterben
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("exit");
        _tray?.Dispose();
        _overlay?.Close();
        _state?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
