# LunaTranscribe für Windows

Lokale Diktier-App für den Windows-Infobereich (Tray). Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf der CPU (ONNX Runtime) mit **Parakeet TDT v3** oder **Cohere Transcribe**.
Dazu ein **Meeting-Modus**: Gespräch aufnehmen (Mikrofon + Systemaudio), danach Sprecher erkennen und transkribieren.
Ein dritter Hotkey mit **Nachbearbeitung**: Ein lokales Sprachmodell (LFM2 oder Qwen3, llama.cpp) bereinigt das Diktat –
Rechtschreibung, Grammatik, Füllwörter, Wiederholungen, Versprecher – bevor es eingefügt wird.
Ein **eigenes Wörterbuch** sorgt dafür, dass Namen und Fachbegriffe richtig geschrieben werden – und **lernt aus deinen
Korrekturen**. Meetings landen als JSON in einer **Meeting-Bibliothek** in einem frei wählbaren Ordner und lassen sich auf
Knopfdruck über die **Claude CLI zusammenfassen** (die einzige, freiwillige Ausnahme vom Local-First-Prinzip). Über den
lokalen MCP-Server **luna-mcp** kann Claude die Meetings durchsuchen und lesen.

Alles lebt im Infobereich; ein **Hauptfenster** (Übersicht, Meetings, Wörterbuch, Verlauf, Einstellungen) öffnest du über
das Tray-Symbol. Design „Mondnacht“: warm und verspielt, Schriften Fraunces + Figtree, hell und dunkel (folgt dem
Windows-Farbmodus).

Windows-Portierung der macOS-App [LunaTranscribe](https://github.com/maximilianhofmeister1/LunaTranscribe) – gleiche
Funktionen, gleiche Struktur, gleiches Design. Das Meeting-Format (`meeting.json`) ist identisch: Ein Meeting-Ordner kann
zwischen Mac und PC geteilt werden (z. B. über einen Cloud-Ordner).

## Voraussetzungen

- Windows 10 (2004) oder Windows 11, 64-bit
- Zum Ausführen: nichts – der Release-Build ist self-contained (bringt .NET mit).
- Zum Bauen: .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`
- RAM: ~1 GB für Parakeet (Standard), ~4 GB für Cohere; Nachbearbeitung zusätzlich ~0,8 GB (LFM2) bis ~3 GB (Qwen3 4B)

## Einrichten

1. Repository klonen
2. `LunaTranscribe.sln` in Visual Studio 2022 / Rider öffnen **oder** im Terminal:
   ```powershell
   dotnet run --project LunaTranscribe -c Release
   ```
   (Release: ONNX- und llama.cpp-Inferenz sowie Audio-Resampling sind im Debug-Build spürbar langsamer.)

## Release-Build & Installation (für den Alltag)

```powershell
.\scripts\build-release.ps1
```

Baut Release (self-contained, ≈ 300 MB inkl. `mcp\`), installiert nach `%LOCALAPPDATA%\Programs\LunaTranscribe`, legt
eine Startmenü-Verknüpfung an und startet die App. Kein Administrator nötig.

**Autostart:** Einstellungen → Allgemein → „Beim Anmelden automatisch starten“ (Eintrag unter `HKCU\…\Run`;
erscheint in Einstellungen → Apps → Autostart und im Task-Manager). Erst aktivieren, wenn die App aus dem
Installationsordner läuft – der Eintrag zeigt auf den aktuellen Pfad der EXE.

## Erster Start

1. Falls Windows den Mikrofonzugriff für Desktop-Apps blockiert, zeigen Popover und Übersicht eine Warnung mit
   Direktlink in die Einstellungen (Datenschutz → Mikrofon → „Desktop-Apps den Zugriff erlauben“).
2. Das Modell wird einmalig von Hugging Face geladen (Parakeet ≈ 0,7 GB – Standard; Cohere ≈ 2,9 GB).
   Fortschritt steht im Popover, in der Seitenleiste und auf der Übersicht. Ablage: `%LOCALAPPDATA%\LunaTranscribe\Models`.
   Abgebrochene Downloads werden fortgesetzt. Danach braucht die App kein Internet mehr.
3. Das Sprachmodell für die Nachbearbeitung (LFM2 ≈ 0,7 GB) lädt, sobald ein Hotkey für „Diktieren + Nachbearbeitung“
   gesetzt ist, sonst bei der ersten Nachbearbeitung. Solange es lädt, wird der Rohtext eingefügt.
4. Die Sprecher-Modelle (~35 MB) werden beim ersten Meeting einmalig geladen.
5. **F2** ist der Standard-Hotkey. Er wird von LunaTranscribe abgefangen und nicht an das aktive Programm
   weitergereicht (F2 löst also z. B. in Excel nicht mehr „Zelle bearbeiten“ aus). Anderer Hotkey:
   Einstellungen → Diktat → ins Feld klicken → Kombination drücken.

## Bedienung

- **Gedrückt halten** (Standard): Hotkey halten, sprechen, loslassen → Text erscheint.
- **Umschalten**: 1× drücken = Start, nochmal = Stopp.
- **Abbrechen**: Escape oder das X im Overlay – verwirft Aufnahme bzw. laufende Transkription.
- **Wiedergabe pausieren**: Läuft Musik/Video (Spotify, Medien-App, Browser …), wird sie beim Aufnahmestart pausiert und
  nach dem Stopp fortgesetzt – nur, wenn die App sie selbst pausiert hat. Abschaltbar unter Einstellungen → Diktat.
  Technik: Windows System Media Transport Controls (WinRT).
- Zweiter Hotkey mit eigener Sprache, dritter Hotkey für Diktat + Nachbearbeitung: Einstellungen → Diktat.
- **Tray-Symbol**: Linksklick = Popover (Status, Diktat, Meeting, letzte Diktate), Rechtsklick = Kontextmenü,
  Doppelklick = Hauptfenster. Das Symbol zeigt Aufnahme, Transkription, Nachbearbeitung und ein laufendes Meeting.
- **Hauptfenster**: Übersicht (heutige Wörter, Modellstatus, Tastenkürzel, letzte Meetings), Meetings, Wörterbuch,
  Verlauf, Einstellungen. Frei skalierbar, merkt sich Größe und Position. Schließen beendet die App nicht.
- **Overlay**: eine Pille in Nachtblau am unteren Bildschirmrand – „Ich höre zu…“ mit lebendiger Wellenform,
  „Transkribiere…“, „Formuliere…“; sie nimmt nie den Fokus.

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

**RAM sparen – Modelle nach Inaktivität entladen** (Einstellungen → Modelle, Standard 10 Min; nur unter Windows): Das
geladene Parakeet-Modell belegt ~750 MB. Nach der eingestellten Zeit ohne Diktat wird es entladen (App dann ~45 MB).
Beim nächsten Hotkey startet die Aufnahme **sofort**, das Modell lädt parallel (ca. 6–9 s von SSD); die Transkription
wartet nur, falls das Diktat kürzer war als das Nachladen. Overlay zeigt dann „Modell wird geladen…“. Dieselbe Zeit gilt
für das Sprachmodell der Nachbearbeitung. Während ein Meeting verarbeitet wird, bleibt das Modell geladen.

## Diktat mit Nachbearbeitung

Dritter Hotkey (Einstellungen → Diktat → „Diktieren + Nachbearbeitung“, kein Default) oder im Popover „Nachbearbeiten“:
gleiches Diktat in der Hauptsprache, aber vor dem Einfügen bereinigt ein lokales Sprachmodell den Text. Das Overlay zeigt
„Ich höre zu… → Transkribiere… → Formuliere…“.

Was das Modell tut: Rechtschreibung, Grammatik, Zeichensetzung; Füllwörter („äh“, „halt“, „sozusagen“), Stotterer,
Wortwiederholungen und Selbstkorrekturen raus. Was es **nicht** tut: umformulieren, kürzen, ergänzen, übersetzen, Fragen im
Text beantworten. Die **vollständige Anweisung** ist unter Einstellungen → Nachbearbeitung editierbar (`{language}` wird
durch die Diktatsprache ersetzt); „Auf Standard zurücksetzen“ stellt die mitgelieferte Fassung wieder her.

**Sicherheitsnetz:** Schlägt die Nachbearbeitung fehl (Modell lädt noch, Fehler, Timeout 20 s, unplausible Antwort), wird
der Rohtext eingefügt und das Overlay sagt es. Ein Diktat geht nie verloren. Im Verlauf sind nachbearbeitete Einträge
markiert; der Rohtext lässt sich aufklappen.

Modelle (llama.cpp über [LLamaSharp](https://github.com/SciSharp/LLamaSharp) 0.27, CPU, GGUF Q4_K_M). Richtwerte für ein
Diktat mit 1–2 Sätzen auf einer 4-Kern-Laptop-CPU (Ryzen 5 3500U) – schnellere CPUs brauchen entsprechend weniger:

| Modell | Download | RAM | Dauer | Anmerkung |
|---|---|---|---|---|
| **LFM2 1.2B** (Standard) | ~0,7 GB | ~0,8 GB | ~2–4 s | Schnellstes brauchbares Modell; verschmilzt Selbstkorrekturen gern falsch |
| **Qwen3 1.7B** | ~1,1 GB | ~1,5 GB | ~4–8 s | Konservativ: lässt Selbstkorrekturen lieber stehen; gutes Englisch |
| **Qwen3 4B** | ~2,5 GB | ~3 GB | ~10–20 s | Löst Selbstkorrekturen innerhalb eines Satzes; auf schwachen CPUs oft im Timeout |

Apple Intelligence aus der macOS-Version gibt es unter Windows nicht. Der feste Anfang des Prompts (Anweisung +
Beispiele) wird nach dem Laden einmal vorberechnet; danach rechnet jedes Diktat nur noch seinen eigenen Teil.

```powershell
LunaTranscribe.exe --refine-selftest "ähm also ich wollte sagen dass dass wir morgen um elf uhr das meeting haben" --model lfm2_1_2b --language de
```

## Eigenes Wörterbuch

Hauptfenster → **Wörterbuch**: Begriffe, die das Modell regelmäßig falsch schreibt, mit der richtigen Schreibweise
eintragen – optional mit den Varianten, als die sie erkannt werden („LunaTranscribe“ ← „Luna Transcribe, Luna transkribe“).
Gilt für **Diktat und Meeting**; nach der Nachbearbeitung wird es erneut angewendet (das Sprachmodell kann einen Begriff
also nicht „zurückkorrigieren“). Varianten und der Begriff in falscher Groß-/Kleinschreibung werden nur an Wortgrenzen
ersetzt, unabhängig von Groß-/Kleinschreibung und Leerzeichen.

Das **akustische Boosting** der macOS-Version (FluidAudio-CTC-Modell, das am Audio prüft, ob ein Begriff gesagt wurde)
gibt es unter Windows noch nicht – typische Fehlschreibungen deshalb als Varianten eintragen.

**Lernendes Wörterbuch:** Korrigierst du ein Wort im **Verlauf** (Stift-Knopf) oder in einem **Meeting-Transkript**,
vergleicht LunaTranscribe vorher und nachher Wort für Wort und schlägt passende Einträge vor („Weglap“ → „weclapp“). Die
Vorschläge erscheinen oben auf der Seite **Wörterbuch** (mit Zähler in der Seitenleiste): **Übernehmen**, Begriff vorher
anpassen, oder **Ignorieren** (kommt nicht wieder). Nichts wird automatisch aktiv.

## Meeting-Modus

Popover → Meeting **Starten** (oder Übersicht → „Meeting starten“). Während des Meetings wird nur aufgenommen
(16 kHz mono, 16 bit in eine WAV-Datei, kaum CPU-Last) – **Diktieren per Hotkey funktioniert währenddessen ganz normal**.
Escape wirkt nur auf das Diktat; ein Meeting wird ausschließlich über Popover/Übersicht beendet oder verworfen.

**Systemaudio** (Einstellungen → Meetings, Standard an): Die Wiedergabe von Teams, Zoom, Browser usw. wird per
WASAPI-Loopback vom Standard-Ausgabegerät mitgeschnitten und mit dem Mikrofon gemischt – Windows braucht dafür keine
eigene Berechtigung. Kopfhörer empfohlen, sonst nimmt das Mikrofon die Lautsprecher zusätzlich auf. Ohne Wiedergabe liefert
Windows keine Pakete; die Aufnahme füllt dann Stille auf (das Mikrofon ist der Taktgeber).

**Meeting beenden** reiht die Aufnahme in die **Hintergrund-Verarbeitung** ein – du kannst **sofort das nächste Meeting
starten**. Verarbeitet wird Meeting für Meeting (Fortschritt im Popover unter „Im Hintergrund“, in der Seitenleiste und
oben auf der Seite Meetings):

1. Sprecher-Erkennung: sherpa-onnx (pyannote segmentation 3.0) findet Sprachabschnitte und Wechsel, danach clustert
   LunaTranscribe ~1,5-s-Fenster anhand von CAM++-Stimm-Embeddings neu (die eingebaute Zuordnung von sherpa warf ähnliche
   Stimmen zusammen).
2. Zusammenhängende Redebeiträge (Turns) werden gebildet und einzeln mit der gewählten Engine transkribiert.
3. Das Meeting wird in der **Meeting-Bibliothek** gespeichert; eine **Windows-Mitteilung** meldet es, ein Klick öffnet es.

Die Warteschlange liegt unter `%LOCALAPPDATA%\LunaTranscribe\Queue`. **Beendest du die App mit wartenden Meetings, geht
es beim nächsten Start weiter.** Auch eine Aufnahme, die beim Absturz noch lief, wird beim nächsten Start gerettet und
eingereiht. Schlägt eine Verarbeitung fehl, bleibt die Aufnahme erhalten: „Erneut versuchen“ oder „Verwerfen“.

**Teilnehmerzahl** (Popover, Standard „automatisch“, bis zum Beenden änderbar) zwingt die Aufteilung auf genau diese
Zahl – hilfreich bei ähnlichen Stimmen. Richtwerte auf einer 4-Kern-Laptop-CPU: Sprecher-Erkennung ≈ 0,1× Echtzeit,
Transkription mit Parakeet ≈ 0,4× Echtzeit (1 h Meeting ≈ 30 min Verarbeitung im Hintergrund).

### Meeting-Bibliothek

Jedes Meeting ist ein Ordner im **Meeting-Ordner** (Standard `Dokumente\LunaTranscribe Meetings`, frei wählbar unter
Einstellungen → Meetings):

```
2026-09-25 14-03 Planung Q4 · a1b2c3\
  meeting.json   Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit, versioniert, Mac-kompatibel)
  summary.md     Zusammenfassung als Markdown – sobald vorhanden
  audio.wav      nur mit „Aufnahme behalten“
```

Die Seite **Meetings** zeigt die Bibliothek nach Monaten gruppiert, mit Volltextsuche. Im Meeting: Titel umbenennen,
Sprecher benennen, einzelne Beiträge korrigieren (speist das lernende Wörterbuch) – alles wird automatisch gespeichert.
TXT/Markdown gibt es als Export (mit oder ohne Zusammenfassung), dazu Kopieren, „Im Explorer zeigen“ und Löschen
(Papierkorb). Ändert Claude (luna-mcp) ein Meeting, während es offen ist, werden die Änderungen zusammengeführt.

- **Ordner wechseln**: Die App fragt, ob vorhandene Meetings mitziehen sollen.
- **Ordner nicht erreichbar** (externes Laufwerk ab): Neue Meetings landen vorübergehend in
  `%LOCALAPPDATA%\LunaTranscribe\Unsaved` und ziehen beim nächsten Einlesen automatisch um.

### Zusammenfassung mit Claude

Im Meeting auf **Zusammenfassen** klicken: LunaTranscribe ruft die lokal installierte **Claude CLI** auf (`claude -p`,
ohne Werkzeuge, ohne MCP, ohne Projektkontext, ohne gespeicherte Sitzung) und legt Kurzfassung, Themen, Entscheidungen,
Aufgaben und offene Fragen im Tab „Zusammenfassung“ und als `summary.md` ab. Dauer je nach Länge 10–60 s.

- **Voraussetzung**: Claude CLI installiert und angemeldet (einmal `claude` starten und anmelden). Die App sucht sie in
  `%USERPROFILE%\.local\bin`, bei npm (`%APPDATA%\npm`), unter Programme und im PATH; ein eigener Pfad und ein Modell
  (z. B. `sonnet`) lassen sich unter Einstellungen → Meetings setzen, „Testen“ prüft Pfad und Version.
- **Anweisung**: Einstellungen → Meetings → „Anweisung für die Zusammenfassung“, frei editierbar.
- **Local-First bleibt ehrlich**: Für die Zusammenfassung geht der Text des Meetings über dein Claude-Konto an Anthropic.
  Beim ersten Mal erklärt ein Hinweis das. Vertrauliche Meetings (Schloss-Knopf) werden nie geschickt.

```powershell
LunaTranscribe.exe --summary-selftest "<Meeting-Ordner>\meeting.json" [--model sonnet] [--cli <pfad\zu\claude.exe>]
```

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

- Diktat-Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Meeting-Audio liegt während der Aufnahme unter `%LOCALAPPDATA%\LunaTranscribe\Recordings`, danach in der Warteschlange,
  und wird nach dem Speichern gelöscht – außer „Aufnahme behalten“ ist aktiv, dann wandert die WAV in den Meeting-Ordner.
- Der Rohtext eines Diktats mit Nachbearbeitung geht nur in das lokale Sprachmodell auf diesem PC.
- Keine Analytics, kein Account. Netzwerk nur für die einmaligen Modell-Downloads (ASR, Sprecher, Sprachmodell, optional
  Embedding-Modell) – und, **nur auf Klick**, für eine Meeting-Zusammenfassung über die Claude CLI. Was Claude über
  luna-mcp liest, siehe oben – nur nach Freigabe.
- Wörterbuch und gelernte Vorschläge liegen in `%APPDATA%\LunaTranscribe\settings.json`, nicht in der Cloud.
- „Letzte Diktate“ (Verlauf) leben nur im Arbeitsspeicher (abschaltbar unter Einstellungen → Datenschutz).
- Die Übersicht zählt „Wörter/Diktate heute“ über einen eigenen Tageszähler – nur Datum und zwei Zahlen, kein Text.
- `%LOCALAPPDATA%\LunaTranscribe\luna.log` enthält nur Zeitmessungen und Fehler – nie Audio oder Text.

## Unterschiede zu macOS

- **Keine Bedienungshilfen-Berechtigung** nötig: `SendInput` (Strg+V) funktioniert immer. Einzige Ausnahme:
  In Programme, die als Administrator laufen, kann nur eingefügt werden, wenn LunaTranscribe ebenfalls als
  Administrator läuft (Windows UIPI).
- **Mikrofon-Berechtigung** ist ein globaler Schalter in den Windows-Einstellungen, kein App-Dialog.
  Die App prüft ihn alle 2 s (Registry `CapabilityAccessManager\ConsentStore\microphone`) und zeigt eine Warnung.
- **Hotkey**: Low-Level-Tastatur-Hook (`WH_KEYBOARD_LL`) statt Carbon-Hotkeys – liefert auch das Loslassen
  der Taste (nötig für „Gedrückt halten“) und verschluckt die Hotkey-Taste. Escape wird nur beobachtet.
- **Systemaudio** per WASAPI-Loopback statt Core-Audio-Prozess-Tap – ohne Berechtigungsdialog.
- **Sprechererkennung** mit sherpa-onnx (pyannote 3.0 + CAM++) und eigener Nachclusterung statt FluidAudio (pyannote community-1).
- **Nachbearbeitung** mit llama.cpp (GGUF, CPU) statt MLX (GPU); kein Apple Intelligence.
- **Kein akustisches Wörterbuch-Boosting** (nur Varianten ersetzen).
- **Modelle nach Inaktivität entladen** (ASR und Sprachmodell) – nur unter Windows.
- **Abgebrochene Meeting-Aufnahmen** (Absturz) werden beim nächsten Start gerettet statt gelöscht.
- **Medien pausieren** über die WinRT-API `GlobalSystemMediaTransportControlsSessionManager` – kein
  gebündelter Adapter nötig.
- **Overlay**: WPF-Fenster mit `WS_EX_NOACTIVATE` (nimmt nie den Fokus), unten mittig auf dem Bildschirm
  des aktiven Fensters.
- **Schriften**: Fraunces und Figtree sind variable Fonts; WPF kann keine Variationsachsen setzen, deshalb liegen statische
  Instanzen mit den Achsenwerten der macOS-Version bei („Luna Display“, „Luna Title“, „Luna Text“, erzeugt mit
  `scripts/make-fonts.py`, OFL).
- **Töne** werden beim Start synthetisiert (keine Systemsounds „Pop“/„Tink“ unter Windows).
- **Mitteilungen** über das Tray-Symbol (Windows zeigt sie als Toast).
- **Startlatenz**: Das Öffnen des Mikrofons dauert je nach Treiber ~60–500 ms (Realtek: ~500 ms, weil der
  Endpunkt aus dem Leerlauf aufwacht). Der Start-Ton erklingt genau dann, wenn das Mikro live ist – also kurz
  warten, bis es „ploppt“. Details stehen im Log (`device resolved` / `StartRecording returned`).

## Entwickler-Selbsttests

Statt eines App-Test-Projekts (Unit-Tests gibt es für `LunaCore` und `luna-mcp`: `dotnet test LunaCore.Tests`,
`dotnet test LunaMCP.Tests`). Ausgabe auf der Konsole und im Log, beenden sich selbst:

```powershell
LunaTranscribe.exe --ui-snapshot C:\temp\luna-ui                  # alle Seiten, Popover, Overlay – hell und dunkel als PNG
LunaTranscribe.exe --refine-selftest "<Text>" --model lfm2_1_2b --language de
LunaTranscribe.exe --diarize-selftest dialog.wav [--speakers 2]    # Sprecher + Transkript einer Datei
LunaTranscribe.exe --meeting-selftest 20                           # 20 s Mikrofon + Systemaudio aufnehmen und verarbeiten
LunaTranscribe.exe --queue-selftest a.wav b.wav [--fake]           # Hintergrund-Verarbeitung, Neustart, Verwerfen
LunaTranscribe.exe --summary-selftest meeting.json                 # Achtung: Text geht an Anthropic
LunaTranscribe.exe --make-icon LunaTranscribe.ico [vorschau.png]   # App-Icon aus dem Code zeichnen
```

## Struktur

```
LunaTranscribe/        WPF-App (net8.0-windows)
  App.xaml(.cs)        Einstieg: Single-Instance, Theme, Tray, Overlay, Hauptfenster bei Bedarf, Selbsttests
  App/                 AppState (Wurzel: Hotkey → Aufnahme → Engine → Nachbearbeitung → Einfügen; Meetings, Suche), SelfTests
  Audio/               AudioRecorder (Diktat, RAM), MeetingRecorder (Mikrofon + Loopback → WAV), AudioDevices, WavFile
  Engine/              ITranscriptionEngine, SherpaEngineBase, Parakeet/Cohere, ModelManager, ModelDownloader, TranscriptionVocabulary
  Hotkey/              Hotkey, KeyboardHook (WH_KEYBOARD_LL, drei Slots)
  Meeting/             MeetingSession (Aufnahme), MeetingProcessingQueue, SpeakerDiarizer, MeetingTranscriber,
                       TranscriberPipeline, MeetingStore, MeetingDocument, MeetingExporter, Selbsttests
  Output/              TextInjector (Zwischenablage + Strg+V), Permissions, MediaPlaybackController, MeetingNotifier, Sounds, Log
  Refine/              RefinerKind, RefinePrompt, LlamaRefiner, RefinerManager, RefineSelfTest
  Search/              SearchIndexer (luna-mcp --index), ClaudeConnection, ClaudeActivity
  Settings/            SettingsStore (JSON + Autostart), HotkeyRecorder, DailyDictationStats
  Summary/             ClaudeCli, MeetingSummarizer, SummarySelfTest
  UI/Theme/            Mondnacht: LunaTheme (Farben hell/dunkel), Styles.xaml, Bausteine, LunaIcon, AppIconArt
  UI/MainWindow/       Hauptfenster (eigene Titelleiste, Seitenleiste), MainRouter
  UI/Pages/            Übersicht, Meetings (+ Detail), Wörterbuch, Verlauf, Settings/ (ein Bereich pro Datei)
  UI/                  TrayIcon, MenuPopup (Popover), OverlayWindow, UISnapshotSelfTest
  Assets/              Icon, Schriften (OFL)
LunaCore/              gemeinsamer Kern (Modell, Bibliothek, Wörterbuch-Logik, Zusammenfassungs-Prompt, Suche)
LunaMCP/               luna-mcp.exe: MCP-Server (stdio), Tools/Ressourcen/Prompts, Bestätigungsdialog, ONNX-Embeddings
LunaCore.Tests/, LunaMCP.Tests/   xUnit
scripts/               build-release.ps1, make-fonts.py
```
