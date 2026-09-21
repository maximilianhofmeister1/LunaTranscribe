# LunaTranscribe

Lokale Diktier-App für die macOS-Menüleiste. Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf Apple Silicon (CoreML / Neural Engine) mit **Cohere Transcribe** oder **Parakeet TDT v3**.
Dazu ein **Meeting-Modus**: Gespräch aufnehmen (Mikrofon + Systemaudio), danach Sprecher erkennen und als Text/Markdown exportieren.
Und ein dritter Hotkey mit **Nachbearbeitung**: Ein lokales Sprachmodell (LFM2, Qwen3 oder Apple Intelligence) bereinigt
das Diktat – Rechtschreibung, Grammatik, Füllwörter, Wiederholungen, Versprecher – bevor es eingefügt wird.

## Voraussetzungen

- macOS 15+, Apple Silicon
- Xcode 16+ (getestet mit 27.0) inkl. **Metal Toolchain** (seit Xcode 26 separat: Xcode → Settings → Components,
  oder `xcodebuild -downloadComponent MetalToolchain`) – nötig, weil MLX seine GPU-Kernel beim Build kompiliert
- `brew install xcodegen` (nur nötig, wenn du `project.yml` änderst)

## Einrichten

1. `xcodegen generate` (nur nötig, wenn `LunaTranscribe.xcodeproj` fehlt oder `project.yml` geändert wurde)
2. `LunaTranscribe.xcodeproj` in Xcode öffnen
3. Target **LunaTranscribe → Signing & Capabilities → Team** auswählen.
   Ein kostenloses Apple-ID-„Personal Team“ reicht. Wichtig, damit die Signatur stabil bleibt –
   sonst fragt macOS nach jedem Build erneut nach den Berechtigungen.
4. ⌘R

## Release-Build & Installation (für den Alltag)

```bash
./scripts/build-release.sh
```

Baut Release, signiert mit dem im `project.yml` hinterlegten Apple-Development-Team, installiert nach
`/Applications/LunaTranscribe.app` und startet die App. Weil die Signatur stabil ist, behält macOS die
Mikrofon- und Bedienungshilfen-Berechtigungen über Updates hinweg.

**Autostart:** Einstellungen → Allgemein → „Beim Anmelden automatisch starten“ (nutzt `SMAppService`;
erscheint in Systemeinstellungen → Allgemein → Anmeldeobjekte). Erst aktivieren, wenn die App aus
`/Applications` läuft – sonst zeigt der Eintrag auf den Xcode-Build-Ordner.

Kein Apple-Developer-Programm nötig: Ein Development-Zertifikat (kostenlose Apple-ID reicht) genügt für
den eigenen Mac. Erst für die Weitergabe an andere bräuchte es Developer ID + Notarisierung.

## Erster Start

1. **Mikrofon**-Dialog erlauben.
2. **Bedienungshilfen**-Dialog → „Systemeinstellungen öffnen“ → LunaTranscribe aktivieren.
   (Nötig, damit die App ⌘V an andere Apps senden darf.)
3. Das Modell wird einmalig von Hugging Face geladen (Parakeet ≈ 0,5 GB – Standard; Cohere ≈ 2 GB).
   Fortschritt steht im Menüleisten-Popover. Ablage: `~/Library/Application Support/LunaTranscribe/Models`.
   Danach braucht die App kein Internet mehr.
   Nach dem Laden folgt ein einmaliges **Warm-up (ca. 1 Minute)** – dabei kompiliert macOS das Modell
   für die Neural Engine und cacht das Ergebnis. Spätere Starts dauern nur wenige Sekunden.
   Parallel lädt das Sprachmodell für die Nachbearbeitung (LFM2 ≈ 0,7 GB – Standard; Qwen3 ≈ 1 GB);
   Fortschritt ebenfalls im Popover. Solange es lädt, fügt der dritte Hotkey den Rohtext ein.
4. **Systemaudio**-Dialog (erst beim ersten Meeting mit aktivierter Systemaudio-Aufnahme): „Erlauben“.
   Steht anschließend in Systemeinstellungen → Datenschutz & Sicherheit → Bildschirm- & Systemaudio-Aufnahme.
   Die Sprecher-Modelle (< 100 MB) werden beim ersten Meeting-Ende einmalig geladen.
5. **F2** ist der Standard-Hotkey. Damit F2 ohne `fn` funktioniert:
   Systemeinstellungen → Tastatur → Tastaturkurzbefehle → Funktionstasten → „F1, F2 usw. als Standard-Funktionstasten“.
   Alternativ `fn`+F2 drücken oder in den Einstellungen einen anderen Hotkey wählen.

## Bedienung

- **Gedrückt halten** (Standard): Hotkey halten, sprechen, loslassen → Text erscheint.
- **Umschalten**: 1× drücken = Start, nochmal = Stopp.
- **Abbrechen**: Escape oder das X im Overlay – verwirft Aufnahme bzw. laufende Transkription.
- **Wiedergabe pausieren**: Läuft Musik/Video (Music, Spotify, Browser …), wird sie beim Aufnahmestart
  pausiert und nach dem Stopp fortgesetzt – nur, wenn die App sie selbst pausiert hat.
  Abschaltbar unter Einstellungen → Audio. Technik: `Vendor/MediaRemoteAdapter` (BSD-3), siehe dortige README.
- Zweiter Hotkey mit eigener Sprache: Einstellungen → Tastenkürzel.

## Diktat mit Nachbearbeitung

Dritter Hotkey (Einstellungen → Tastenkürzel → „Diktieren + Nachbearbeitung“, kein Default): gleiches Diktat in der
Hauptsprache, aber vor dem Einfügen bereinigt ein lokales Sprachmodell den Text – wie die Kernfunktion von Superwhisper.
Das Overlay zeigt „Aufnahme… → Transkribiere… → Formuliere…“.

Was das Modell tut: Rechtschreibung, Grammatik, Zeichensetzung; Füllwörter („äh“, „halt“, „sozusagen“), Stotterer,
Wortwiederholungen und Selbstkorrekturen raus. Was es **nicht** tut: umformulieren, kürzen, ergänzen, übersetzen,
Fragen im Text beantworten. Wortwahl, Ton und Du/Sie bleiben. Die **vollständige Anweisung** an das Modell ist unter Einstellungen → Nachbearbeitung
editierbar (`{language}` wird durch die Diktatsprache ersetzt); „Auf Standard zurücksetzen“ stellt die mitgelieferte
Fassung wieder her. Die Beispielpaare (Deutsch/Englisch) hängt die App automatisch als Chat-Runden an.
Im Menü gibt es neben „Diktat starten“ auch „Diktat + Nachbearbeitung“ – identisch mit Hotkey 3.
Meeting-Transkripte sind nicht betroffen.

**Sicherheitsnetz:** Schlägt die Nachbearbeitung fehl (Modell lädt noch, Fehler, Timeout 20 s, unplausible Antwort),
wird der Rohtext eingefügt und das Overlay sagt es. Ein Diktat geht nie verloren. Im Verlauf („Letzte Diktate“) sind
nachbearbeitete Einträge mit ✨ markiert; der Rohtext steht im Tooltip.

Modelle (Einstellungen → Nachbearbeitung), Richtwerte für ein Diktat mit 2–3 Sätzen auf einem M3:

| Modell | Download | RAM | Dauer | Anmerkung |
|---|---|---|---|---|
| **LFM2 1.2B** (Standard) | ~0,7 GB | ~0,9 GB | ~0,6 s | Schnellstes brauchbares Modell; verschmilzt Selbstkorrekturen gern falsch („um zehn Uhr und elf Uhr“) |
| **Qwen3 1.7B** | ~1 GB | ~1,3 GB | ~1 s | Konservativ: lässt Selbstkorrekturen lieber stehen („um zehn, nein um elf Uhr“); gutes Englisch |
| **Qwen3 4B** | ~2,3 GB | ~2,8 GB | ~2–2,5 s | Löst Selbstkorrekturen innerhalb eines Satzes („am Montag nein am Dienstag … bei mir nein bei dir“ → „am Dienstag … bei dir“) |
| **Apple Intelligence** | – | – | ~1 s | Apples Systemmodell (macOS 26+), läuft auf der Neural Engine. Ausgegraut, solange Apple Intelligence nicht aktiv ist |

Grenze aller drei: Wird ein **ganzer Satz** wiederholt und korrigiert („Heute ist ein schöner Tag. Nein, morgen ist ein
schöner Tag …“), lesen sie das „nein“ als Fortsetzung und behalten beide Sätze – dafür bräuchte es ein größeres Modell
außerhalb des 2-Sekunden-Ziels. Selbstkorrekturen eines einzelnen Wortes/Details funktionieren ab Qwen3 4B verlässlich.

LFM2 und Qwen3 laufen über [MLX](https://github.com/ml-explore/mlx-swift) auf der **GPU** (Metal, Unified Memory) – die
Neural Engine ist nur über CoreML erreichbar. Beide bleiben nach dem Start geladen, damit der Hotkey sofort reagiert.

**Apple Intelligence** setzt voraus, dass es in den Systemeinstellungen aktiviert ist und macOS das Modell geladen hat.
Fehlt der Eintrag „Apple Intelligence“ in den Systemeinstellungen, liegt das meist daran, dass **Siri-Sprache und
Gerätesprache nicht übereinstimmen** (z. B. Siri Englisch, Gerät Deutsch) – beide auf dieselbe unterstützte Sprache
stellen. Seit macOS 15.4 ist Apple Intelligence auch in der EU verfügbar. Die Einstellungen zeigen den konkreten Grund.
Apples Inhaltsfilter können in seltenen Fällen einen Text ablehnen – dann greift das Sicherheitsnetz.

## Meeting-Modus

Menüleiste → **Meeting starten**. Während des Meetings wird nur aufgenommen (16 kHz mono in eine
temporäre WAV-Datei, kaum CPU-Last) – **Diktieren per Hotkey funktioniert währenddessen ganz normal**
und landet ebenfalls im Meeting-Transkript. Escape wirkt nur auf das Diktat; ein Meeting wird
ausschließlich über das Menü beendet oder verworfen.

**Meeting beenden** startet die Verarbeitung (Fortschritt im Menü):

1. Sprecher-Erkennung (Diarization) mit FluidAudios Offline-Pipeline (pyannote community-1, CoreML).
2. Zusammenhängende Redebeiträge (Turns) werden gebildet und einzeln mit der gewählten Engine transkribiert.
3. Das **Ergebnisfenster** öffnet sich: Sprecher umbenennen („Sprecher 1“ → Name), Beiträge lesen,
   **Als Text / Als Markdown sichern…** oder kopieren. Ein Backup als `.txt` liegt bereits im Ablageordner
   (Standard `~/Documents/LunaTranscribe Meetings`, änderbar unter Einstellungen → Meeting).

Richtwerte: 1 h Meeting ≈ 1–2 min Verarbeitung mit Parakeet, ≈ 5 min mit Cohere. Beim allerersten Mal
kommt die einmalige Kompilierung der Sprecher-Modelle dazu.

**Systemaudio** (Einstellungen → Meeting, Standard an): Für Online-Meetings wird die Wiedergabe von Zoom, Teams,
Browser usw. per Core-Audio-Prozess-Tap mitgeschnitten und mit dem Mikrofon zu einem privaten Aggregate-Device
kombiniert (Core Audio übernimmt die Synchronisation). Kopfhörer empfohlen – sonst nimmt das Mikrofon die
Lautsprecher zusätzlich auf. Wird die Berechtigung verweigert, liefert macOS stille Systemaudio-Spuren ohne
Fehlermeldung; das Meeting läuft dann mit Mikrofon weiter, das Menü zeigt einen Hinweis.

**Sprechererkennung** arbeitet rein akustisch (Stimm-Embeddings) – egal ob eine Stimme über das Mikrofon
oder das Systemaudio kam; zwei Personen an einem Mikrofon werden also getrennt. Das Clustering ist aber
konservativ: Bei leisen, entfernten oder ähnlichen Stimmen landet schnell alles bei „Sprecher 1“. Deshalb im
Menü die **Teilnehmerzahl** vorgeben (Standard „automatisch“, bis zum Beenden änderbar) – das zwingt die
Aufteilung auf genau diese Zahl. Praktisch: Mikrofon zwischen die Personen legen, Überlappungen vermeiden.

Grenzen: Mikrofon und Ausgabegerät werden beim Meeting-Start festgelegt und folgen während der Aufnahme
keinem Gerätewechsel.

## Datenschutz

- Diktat-Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Meeting-Audio liegt während Aufnahme und Verarbeitung temporär unter `~/Library/Caches/LunaTranscribe/Meetings`
  und wird danach gelöscht – außer „Aufnahme behalten“ ist aktiv, dann wandert die WAV in den Ablageordner.
  Transkript-Backups (`.txt`) liegen im Ablageordner.
- Der Rohtext eines Diktats mit Nachbearbeitung geht nur in das lokale Sprachmodell auf diesem Mac.
- Keine Cloud, keine Analytics. Einzige Netzwerkverbindung: der einmalige Modell-Download (ASR + Sprecher-Modelle + Sprachmodell).
- „Letzte Diktate“ im Menü leben nur im Arbeitsspeicher (abschaltbar).

## Struktur

```
LunaTranscribe/
  App/        LunaTranscribeApp (MenuBarExtra), AppState (Hotkey → Aufnahme → Engine → Einfügen; hält MeetingSession)
  Audio/      AudioRecorder (AVAudioEngine → 16 kHz Float32, Sink RAM oder WAV-Datei), AudioDevices (CoreAudio),
              SystemAudioTap (Prozess-Tap + Aggregate-Device für Systemaudio)
  Engine/     TranscriptionEngine-Protokoll, CohereEngine, ParakeetEngine, ModelManager
  Hotkey/     KeyboardShortcuts-Namen (Default F2)
  Meeting/    MeetingSession (Statusmaschine), MeetingTranscriber (Diarizer → Turns → Engine),
              SpeakerDiarizer (FluidAudio Offline-Pipeline), MeetingTranscript (+ txt/md-Export), MeetingExporter
  Output/     TextInjector (Pasteboard + ⌘V), Permissions, MediaPlaybackController (Wiedergabe pausieren)
  Refine/     TextRefiner-Protokoll + RefinerKind, RefinePrompt (Anweisung, Beispielpaar, Antwort-Bereinigung),
              MLXRefiner (LFM2/Qwen3 via MLX), AppleIntelligenceRefiner (Foundation Models), RefinerManager, RefineSelfTest
  Settings/   SettingsStore, AppLanguage, SettingsView
  UI/         MenuBarView, OverlayController (schwebende Statusanzeige), MeetingResultView (+ Fenster-Controller)
Vendor/MediaRemoteAdapter/   Framework + Perl-Skript für Now-Playing-Zugriff (macOS ≥ 15.4), neu bauen: scripts/build-mediaremote-adapter.sh
```

Nachbearbeitung – Designentscheidungen: Zustandslos pro Diktat (kein Chat-Verlauf), greedy, Qwen3 ohne Thinking.
Der Prompt ist Englisch mit expliziter Ausgabesprache; das Diktat steckt in `<dictation>`-Tags, damit kleine Modelle
Fragen im Text nicht beantworten. Das Beispielpaar geht als echte User/Assistant-Runde in den Chat – als Text in der
Anweisung ahmen 1-B-Modelle das Beispielformat nach. `RefinePrompt.sanitize` entfernt Tags/Präambeln/Anführungszeichen
und verwirft Antworten, deren Länge außerhalb 0,4–2× des Originals liegt (→ Rohtext).

Nachbearbeitung ohne Sprechen testen (Prompt-Tuning), kein `open` nötig, da keine Audio-Berechtigung gebraucht wird:

```bash
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --refine-selftest "ähm also ich wollte sagen dass dass wir morgen um elf uhr das meeting haben" --model qwen3_1_7b --language de
```

Meeting-Modus ohne UI testen (Entwicklung): nimmt N Sekunden von Mikrofon + Systemaudio auf, verarbeitet
und schreibt Diagnose (Kanalpegel) und Transkript in die Log-Datei – parallel z. B. `say -v Anna "…"` abspielen:

```bash
open -n /Applications/LunaTranscribe.app --args --meeting-selftest 20 --log /tmp/luna-selftest.log
```

Wichtig: über `open` starten, nicht direkt aus dem Terminal – sonst ordnet macOS die Berechtigung
„Systemaudio aufnehmen“ dem Terminal zu und der Tap liefert nur Stille.

Meeting-Modus – Designentscheidungen: Systemaudio über ein privates Aggregate aus Ausgabegerät (Master/Clock)
+ Mikrofon + Prozess-Tap, gelesen per Core-Audio-IOProc (`DeviceInputStream`), weil `AVAudioEngine` nach
einem Gerätewechsel das Kanal-Layout nicht übernimmt. `tapautostart` ist bewusst **aus** – sonst startet das
Aggregate erst, wenn irgendein Prozess Audio ausgibt, und eine stille Aufnahme bleibt leer.
Sprecher-Zuordnung per **Turn-Slicing** (Turn-Audio einzeln durch
`engine.transcribe`), nicht per Wort-Zeitstempel – so bleibt das Engine-Protokoll unverändert und Cohere
(liefert keine Zeitstempel) funktioniert genauso wie Parakeet. Mögliche Verfeinerung: Token-Alignment über
Parakeets `tokenTimings`. Schwellen für das Zusammenfassen stehen oben in `MeetingTranscriber`.
