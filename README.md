# LunaTranscribe

Lokale Diktier-App für die macOS-Menüleiste. Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf Apple Silicon (CoreML / Neural Engine) mit **Cohere Transcribe** oder **Parakeet TDT v3**.

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
4. **F2** ist der Standard-Hotkey. Damit F2 ohne `fn` funktioniert:
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

## Datenschutz

- Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Keine Cloud, keine Analytics. Einzige Netzwerkverbindung: der einmalige Modell-Download.
- „Letzte Diktate“ im Menü leben nur im Arbeitsspeicher (abschaltbar).

## Struktur

```
LunaTranscribe/
  App/        LunaTranscribeApp (MenuBarExtra), AppState (Hotkey → Aufnahme → Engine → Einfügen)
  Audio/      AudioRecorder (AVAudioEngine → 16 kHz Float32), AudioDevices (CoreAudio)
  Engine/     TranscriptionEngine-Protokoll, CohereEngine, ParakeetEngine, ModelManager
  Hotkey/     KeyboardShortcuts-Namen (Default F2)
  Output/     TextInjector (Pasteboard + ⌘V), Permissions, MediaPlaybackController (Wiedergabe pausieren)
  Settings/   SettingsStore, AppLanguage, SettingsView
  UI/         MenuBarView, OverlayController (schwebende Statusanzeige)
Vendor/MediaRemoteAdapter/   Framework + Perl-Skript für Now-Playing-Zugriff (macOS ≥ 15.4), neu bauen: scripts/build-mediaremote-adapter.sh
```

Post-Processing (Füllwörter entfernen etc.) ist bewusst noch nicht enthalten; der Hook dafür ist
`AppState.stopRecordingAndTranscribe()` zwischen `engine.transcribe` und `TextInjector.insert`.
