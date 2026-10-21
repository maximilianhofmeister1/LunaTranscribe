# LunaTranscribe für Windows

Lokale Diktier-App für den Windows-Infobereich (Tray). Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf der CPU (ONNX Runtime) mit **Parakeet TDT v3** oder **Cohere Transcribe**.

Windows-Portierung der macOS-App [LunaTranscribe](https://github.com/maximilianhofmeister1/LunaTranscribe) – gleiche Funktionen, gleiche Struktur, gleiche Bedienung.

## Voraussetzungen

- Windows 10 (2004) oder Windows 11, 64-bit
- Zum Ausführen: nichts – der Release-Build ist self-contained (bringt .NET mit).
- Zum Bauen: .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`
- ~1,5 GB freier RAM für Parakeet (Standard); ~4 GB für Cohere Transcribe

## Einrichten

1. Repository klonen
2. `LunaTranscribe.sln` in Visual Studio 2022 / Rider öffnen **oder** im Terminal:
   ```powershell
   dotnet run --project LunaTranscribe -c Release
   ```
   (Release: ONNX-Inferenz und Audio-Resampling sind im Debug-Build spürbar langsamer.)

## Release-Build & Installation (für den Alltag)

```powershell
.\scripts\build-release.ps1
```

Baut Release (self-contained, ≈ 190 MB), installiert nach `%LOCALAPPDATA%\Programs\LunaTranscribe`, legt eine
Startmenü-Verknüpfung an und startet die App. Kein Administrator nötig.

Alternativ ohne Installation: `dotnet publish LunaTranscribe -c Release -o publish` und `publish\LunaTranscribe.exe` starten.

**Autostart:** Einstellungen → Allgemein → „Beim Anmelden automatisch starten“ (Eintrag unter `HKCU\…\Run`;
erscheint in Einstellungen → Apps → Autostart und im Task-Manager). Erst aktivieren, wenn die App aus dem
Installationsordner läuft – der Eintrag zeigt auf den aktuellen Pfad der EXE.

## Erster Start

1. Falls Windows den Mikrofonzugriff für Desktop-Apps blockiert, zeigt das Tray-Menü eine Warnung mit
   Direktlink in die Einstellungen (Datenschutz → Mikrofon → „Desktop-Apps den Zugriff erlauben“).
2. Das Modell wird einmalig von Hugging Face geladen (Parakeet ≈ 0,7 GB – Standard; Cohere ≈ 2,9 GB).
   Fortschritt steht im Tray-Tooltip, im Tray-Popover und in den Einstellungen.
   Ablage: `%LOCALAPPDATA%\LunaTranscribe\Models`. Abgebrochene Downloads werden fortgesetzt.
   Danach braucht die App kein Internet mehr. Nach dem Laden folgt ein kurzes Warm-up (wenige Sekunden).
3. **F2** ist der Standard-Hotkey. Er wird von LunaTranscribe abgefangen und nicht an das aktive Programm
   weitergereicht (F2 löst also z. B. in Excel nicht mehr „Zelle bearbeiten“ aus). Anderer Hotkey:
   Einstellungen → Tastenkürzel → ins Feld klicken → Kombination drücken.

## Bedienung

- **Gedrückt halten** (Standard): Hotkey halten, sprechen, loslassen → Text erscheint.
- **Umschalten**: 1× drücken = Start, nochmal = Stopp.
- **Abbrechen**: Escape oder das X im Overlay – verwirft Aufnahme bzw. laufende Transkription.
- **Wiedergabe pausieren**: Läuft Musik/Video (Spotify, Medien-App, Browser …), wird sie beim Aufnahmestart
  pausiert und nach dem Stopp fortgesetzt – nur, wenn die App sie selbst pausiert hat.
  Abschaltbar unter Einstellungen → Audio. Technik: Windows System Media Transport Controls (WinRT).
- Zweiter Hotkey mit eigener Sprache: Einstellungen → Tastenkürzel.
- Tray-Symbol: Linksklick = Popover (Status, Sprache, Engine, Start/Stopp, letzte Diktate),
  Rechtsklick = Kontextmenü, Doppelklick = Einstellungen.

## Engines

| | Parakeet TDT v3 (Standard) | Cohere Transcribe |
|---|---|---|
| Download | ≈ 0,7 GB | ≈ 2,9 GB |
| RAM | ≈ 1 GB | ≈ 4 GB |
| Sprachen | 25 europäische, automatisch erkannt | 14, muss gewählt werden |
| Quelle | `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | `csukuangfj2/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01` |

Beide laufen über [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (INT8, CPU). Die Sprachauswahl wirkt bei
Cohere; Parakeet erkennt die Sprache selbst – die Auswahl filtert dort nur die sinnvollen Sprachen.
Threads: halbe Anzahl logischer Kerne (≈ physische Kerne), max. 8.

**RAM sparen – Modell nach Inaktivität entladen** (Einstellungen → Modell, Standard 10 Min): Das geladene
Parakeet-Modell belegt ~750 MB. Nach der eingestellten Zeit ohne Diktat wird es entladen (App dann ~45 MB).
Beim nächsten Hotkey startet die Aufnahme **sofort**, das Modell lädt parallel (ca. 6–9 s von SSD); die
Transkription wartet nur, falls das Diktat kürzer war als das Nachladen. Overlay zeigt dann „Modell wird geladen…“.

## Datenschutz

- Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Keine Cloud, keine Analytics. Einzige Netzwerkverbindung: der einmalige Modell-Download.
- „Letzte Diktate“ im Menü leben nur im Arbeitsspeicher (abschaltbar).
- `%LOCALAPPDATA%\LunaTranscribe\luna.log` enthält nur Zeitmessungen und Fehler – nie Audio oder Text.

## Unterschiede zu macOS

- **Keine Bedienungshilfen-Berechtigung** nötig: `SendInput` (Strg+V) funktioniert immer. Einzige Ausnahme:
  In Programme, die als Administrator laufen, kann nur eingefügt werden, wenn LunaTranscribe ebenfalls als
  Administrator läuft (Windows UIPI).
- **Mikrofon-Berechtigung** ist ein globaler Schalter in den Windows-Einstellungen, kein App-Dialog.
  Die App prüft ihn alle 2 s (Registry `CapabilityAccessManager\ConsentStore\microphone`) und zeigt eine Warnung.
- **Hotkey**: Low-Level-Tastatur-Hook (`WH_KEYBOARD_LL`) statt Carbon-Hotkeys – liefert auch das Loslassen
  der Taste (nötig für „Gedrückt halten“) und verschluckt die Hotkey-Taste. Escape wird nur beobachtet.
- **Medien pausieren** über die WinRT-API `GlobalSystemMediaTransportControlsSessionManager` – kein
  gebündelter Adapter nötig.
- **Overlay**: WPF-Fenster mit `WS_EX_NOACTIVATE` (nimmt nie den Fokus), unten mittig auf dem Bildschirm
  des aktiven Fensters.
- **Töne** werden beim Start synthetisiert (keine Systemsounds „Pop“/„Tink“ unter Windows).
- **Startlatenz**: Das Öffnen des Mikrofons dauert je nach Treiber ~60–500 ms (Realtek: ~500 ms, weil der
  Endpunkt aus dem Leerlauf aufwacht). Der Start-Ton erklingt genau dann, wenn das Mikro live ist – also kurz
  warten, bis es „ploppt“. Warmhalten ginge nur mit dauerhaft offenem Mikrofon („in Benutzung“-Anzeige) und ist
  bewusst nicht umgesetzt. Details stehen im Log (`device resolved` / `StartRecording returned`).

## Struktur

```
LunaTranscribe/
  App.xaml(.cs)   Einstieg: Single-Instance, Tray, Overlay – kein Hauptfenster
  App/            AppState (Hotkey → Aufnahme → Engine → Einfügen)
  Audio/          AudioRecorder (WASAPI → 16 kHz Float32), AudioDevices (MMDevice)
  Engine/         ITranscriptionEngine, SherpaEngineBase, ParakeetEngine, CohereEngine, ModelManager, ModelDownloader
  Hotkey/         Hotkey (VK + Modifier), KeyboardHook (WH_KEYBOARD_LL, Default F2)
  Output/         TextInjector (Zwischenablage + Strg+V), Permissions, MediaPlaybackController, Sounds, Log
  Settings/       SettingsStore (JSON + Autostart), AppLanguage, SettingsWindow, HotkeyRecorder
  UI/             TrayIcon (NotifyIcon + gezeichnete Symbole), MenuPopup, OverlayWindow
scripts/build-release.ps1
```

Post-Processing (Füllwörter entfernen etc.) ist bewusst noch nicht enthalten; der Hook dafür ist
`AppState.TranscribeAsync()` zwischen `engine.TranscribeAsync` und `TextInjector.InsertAsync`.
