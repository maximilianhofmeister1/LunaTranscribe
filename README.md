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

## Meetings in Claude (MCP)

`luna-mcp.exe` ist ein kleiner **MCP-Server**, der mit der App ausgeliefert wird (Unterordner `mcp\` neben
`LunaTranscribe.exe`). Claude Code oder Claude Desktop starten ihn auf deinem PC; er liest die Meeting-Bibliothek
direkt aus dem Meeting-Ordner – ohne Netzwerk, auch wenn LunaTranscribe gerade nicht läuft. Damit kann Claude:

- **suchen** – nach Stichworten und, wenn eingeschaltet, **inhaltlich** („Wo haben wir über Preiserhöhungen
  gesprochen?“ findet auch „wir müssen teurer werden“); Treffer mit Zeitmarke und Sprecher,
- **lesen** – ganze Meetings oder Ausschnitte (nach Sprecher, Zeitraum), lange Meetings seitenweise,
- **Sprecher benennen** und **Meetings umbenennen** – nur mit Erlaubnis, jede Änderung steht im Protokoll und lässt
  sich rückgängig machen,
- **Meetings löschen** – ausschließlich nach deiner Zustimmung in einem **Windows-Dialog**, den Claude weder auslösen
  noch bestätigen kann („Abbrechen“ ist vorausgewählt, ohne Antwort nach 2 Minuten abgebrochen); das Meeting wandert in
  den Papierkorb,
- dazu Prompts wie Wochenrückblick, Aufgaben je Person, Follow-up-Mail, „Wann wurde X entschieden?“ und
  „Sprecher benennen“ (schlägt Namen mit Belegstelle vor).

**Einrichten**

1. LunaTranscribe → Einstellungen → Datenschutz → **„Meetings für Claude freigeben“**. Ohne diesen Schalter antwortet
   luna-mcp nur mit einem Hinweis (doppeltes Opt-in). Darunter optional **„Claude darf Meetings bearbeiten“**.
2. Claude Code (einmalig in PowerShell oder der Eingabeaufforderung, „Befehl kopieren“ in den Einstellungen liefert ihn
   mit dem richtigen Pfad):
   ```powershell
   claude mcp add --scope user --transport stdio lunatranscribe -- "$env:LOCALAPPDATA\Programs\LunaTranscribe\mcp\luna-mcp.exe"
   ```
   Claude Desktop: „Eintrag kopieren“ und in `%APPDATA%\Claude\claude_desktop_config.json` unter `mcpServers`
   einfügen, Claude Desktop neu starten:
   ```json
   { "mcpServers": { "lunatranscribe": { "command": "C:\\Users\\<du>\\AppData\\Local\\Programs\\LunaTranscribe\\mcp\\luna-mcp.exe" } } }
   ```
3. Optional: **„Inhaltliche Suche“** (gleiche Seite) – lädt einmalig Multilingual E5 Small (~470 MB, ONNX, CPU).
4. „Verbindung testen“ in den Einstellungen – oder im Terminal:
   ```powershell
   & "$env:LOCALAPPDATA\Programs\LunaTranscribe\mcp\luna-mcp.exe" --check      # Einrichtung prüfen (nur lesend)
   & "$env:LOCALAPPDATA\Programs\LunaTranscribe\mcp\luna-mcp.exe" --selftest   # Funktionsprobe mit Beispiel-Meetings
   ```

**Suchindex:** Solange die Freigabe an ist, hält die App den Index aktuell (`%LOCALAPPDATA%\LunaTranscribe\Index\
search.sqlite`, SQLite FTS5 + Vektoren). Dafür startet sie `mcp\luna-mcp.exe --index` im Hintergrund – nie während ein
Meeting verarbeitet wird. luna-mcp rechnet fehlende Vektoren auch selbst nach. Der Index enthält keine Namen und nie
vertrauliche Meetings; „Suchindex löschen“ baut ihn neu auf.

**Was an Anthropic geht:** luna-mcp selbst schickt nichts ins Netz. Was Claude über luna-mcp liest (Suchtreffer,
Transkripte), verarbeitet Claude aber bei Anthropic – wie bei der Zusammenfassung. Beim ersten Einschalten erklärt ein
Hinweis das. **Vertrauliche Meetings** sind für Claude unsichtbar, landen nicht im Suchindex und werden nicht
zusammengefasst. Audio und der Diktat-Verlauf sind nie erreichbar. Das Protokoll „Zuletzt durch Claude“ in den
Einstellungen zeigt jeden Zugriff (nur IDs und Namen, keine Inhalte; `%LOCALAPPDATA%\LunaTranscribe\mcp-activity.log`).
luna-mcp protokolliert Fehler nach `%LOCALAPPDATA%\LunaTranscribe\luna-mcp.log` (und stderr) – stdout gehört allein
dem MCP-Protokoll.

**Warum ein Unterordner:** luna-mcp bringt eine eigene .NET-Runtime und ONNX Runtime 1.31 mit; sherpa-onnx (Spracherkennung)
liefert im App-Ordner eine andere `onnxruntime.dll` (1.28). Im Unterordner `mcp\` kommen sich beide nie in die Quere.

## Datenschutz

- Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Keine Cloud, keine Analytics. Einzige Netzwerkverbindung: der einmalige Modell-Download (auch das optionale
  Embedding-Modell der inhaltlichen Suche). Was Claude über luna-mcp liest, siehe oben – nur nach Freigabe.
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
LunaCore/         gemeinsamer Kern (Modell, Bibliothek, Wörterbuch, Suche: Chunker, KeywordQuery, HybridRanker, SearchIndex)
LunaMCP/          luna-mcp.exe: MCP-Server (stdio), Tools/Ressourcen/Prompts, Bestätigungsdialog, ONNX-Embeddings
LunaCore.Tests/, LunaMCP.Tests/   xUnit
scripts/build-release.ps1
```

Post-Processing (Füllwörter entfernen etc.) ist bewusst noch nicht enthalten; der Hook dafür ist
`AppState.TranscribeAsync()` zwischen `engine.TranscribeAsync` und `TextInjector.InsertAsync`.
