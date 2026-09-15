# LunaTranscribe

Lokale Diktier-App für die macOS-Menüleiste. Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf Apple Silicon (CoreML / Neural Engine) mit **Cohere Transcribe** oder **Parakeet TDT v3**.
Dazu ein **Meeting-Modus**: Gespräch aufnehmen (Mikrofon + Systemaudio), danach Sprecher erkennen und als Text/Markdown exportieren.

## Voraussetzungen

- macOS 15+, Apple Silicon
- Xcode 16+ (getestet mit 26.3)
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
- Keine Cloud, keine Analytics. Einzige Netzwerkverbindung: der einmalige Modell-Download (ASR + Sprecher-Modelle).
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
  Settings/   SettingsStore, AppLanguage, SettingsView
  UI/         MenuBarView, OverlayController (schwebende Statusanzeige), MeetingResultView (+ Fenster-Controller)
Vendor/MediaRemoteAdapter/   Framework + Perl-Skript für Now-Playing-Zugriff (macOS ≥ 15.4), neu bauen: scripts/build-mediaremote-adapter.sh
```

Post-Processing (Füllwörter entfernen etc.) ist bewusst noch nicht enthalten; der Hook dafür ist
`AppState.stopRecordingAndTranscribe()` zwischen `engine.transcribe` und `TextInjector.insert`.

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
