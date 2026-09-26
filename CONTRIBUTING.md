# Mitmachen bei LunaTranscribe

Danke fürs Interesse! LunaTranscribe ist eine lokale Diktier- und Meeting-App für macOS. Beiträge sind willkommen –
bitte lies vorher diese Seite.

## Grundsätze

1. **Local-First.** Audio und Text verlassen den Mac nicht. Neue Funktionen laufen lokal; eine Cloud-Funktion ist
   nur als ausdrückliche, gekennzeichnete Option denkbar (Vorbild: die Claude-Zusammenfassung).
2. **Ein Diktat geht nie verloren.** Scheitert eine Nachbearbeitung, wird der Rohtext eingefügt. Ein Meeting-Transkript
   wird immer gespeichert – notfalls im Rettungsordner.
3. **Lieber nichts als falsch.** Automatische Ersetzungen (Wörterbuch) werden erst aktiv, wenn der Nutzer sie bestätigt.

## Entwicklungsumgebung

- macOS 15+, Apple Silicon, Xcode 16+ mit **Metal Toolchain** (`xcodebuild -downloadComponent MetalToolchain`)
- `brew install xcodegen`
- `xcodegen generate`, dann `LunaTranscribe.xcodeproj` öffnen. Das Xcode-Projekt ist generiert und nicht eingecheckt –
  Änderungen gehören in `project.yml`.
- Signatur: `cp Config/Local.xcconfig.example Config/Local.xcconfig` und die eigene Team-ID eintragen (empfohlen, sonst
  fragt macOS nach jedem Build erneut nach Berechtigungen).

Debug-Build ohne Xcode:

```bash
xcodebuild -project LunaTranscribe.xcodeproj -scheme LunaTranscribe -configuration Debug -destination 'platform=macOS,arch=arm64' -derivedDataPath build/debug build
```

## Konventionen

- **Sprache:** Kommentare, Log-Meldungen, Oberflächentexte und Doku auf **Deutsch**; Bezeichner auf Englisch.
- **Architektur für Modellfamilien:** Protokoll + `Kind`-Enum + `@MainActor`-Manager (`TranscriptionEngine`/
  `EngineKind`/`ModelManager`, `TextRefiner`/`RefinerKind`/`RefinerManager`).
- **UI:** nur Bausteine aus `UI/Theme` (`LunaColor`, `LunaFont`, `lunaCard`, `LunaButtonStyle`, `LunaPage`) –
  keine eigenen Farben oder Schriften in Views. Alles muss im Hell- und Dunkelmodus funktionieren.
- **Nebenläufigkeit:** UI-nahe Klassen sind `@MainActor`; Fortschritts-Callbacks sind `@Sendable` und springen vor dem
  Ändern veröffentlichter Zustände auf den Main Actor.
- **Logs:** über `Log.<Kategorie>` (`App/Log.swift`), keine Diktatinhalte als `.public`.
- **Doku:** README und CLAUDE.md im selben Änderungssatz mitpflegen.

## Tests

**Unit-Tests** gibt es für das Swift-Paket `Packages/LunaCore` (Datenmodell, Meeting-Bibliothek, Suchindex, MCP-Server
über das echte Protokoll inkl. stdio). Sie brauchen weder Xcode noch Mikrofon und laufen auch unter Linux:

```bash
cd Packages/LunaCore && swift test
```

Datenmodell- und Bibliotheksänderungen gehören in `LunaCore` – App und `luna-mcp` teilen sich diesen Code. Für alles
andere gibt es reproduzierbare Entwickler-Selbsttests, die ohne UI laufen und sich selbst beenden:

| Flag | Prüft | Berechtigung |
|---|---|---|
| `--learn-selftest` | Lernendes Wörterbuch (eingebaute Fälle) | keine |
| `--vocab-selftest <wav…> --terms …` | Wörterbuch an Audiodateien | keine |
| `--refine-selftest "<text>"` | Nachbearbeitung (Prompt, Modell, Zeiten) | keine |
| `--summary-selftest [meeting.json]` | Zusammenfassung über die Claude CLI (sendet Text an Anthropic) | keine |
| `--queue-selftest <wav…>` | Hintergrund-Verarbeitung mehrerer Meetings | keine |
| `--ui-snapshot <ordner>` | Rendert alle Seiten hell/dunkel als PNG | keine |
| `--meeting-selftest <sek> --log <datei>` | Aufnahme Mikrofon + Systemaudio → Transkript | über `open -n … --args` starten |
| `luna-mcp --selftest` | MCP-Server: jedes Tool, Ressourcen, Prompts, Embedding-Modell (falls geladen) | keine |
| `luna-mcp --check` | Einrichtung: Meeting-Ordner, Freigaben, Suchindex, Modell | keine |

Vor einem Pull Request: `swift test` in `Packages/LunaCore`, Debug-Build ohne neue Warnungen in `LunaTranscribe/`,
betroffene Self-Tests laufen lassen, bei UI-Änderungen `--ui-snapshot` ansehen.

**luna-mcp:** Im Serverbetrieb gehört stdout ausschließlich JSON-RPC – kein `print`, Logs über `os.Logger`
(Kategorie `mcp`). Schreibende Tools brauchen immer die Revision; Löschen läuft nur über den Bestätigungsdialog
(`UserConfirmer`) und nie endgültig.

## Pull Requests

- Klein und thematisch geschlossen; im Text beschreiben, *warum* etwas geändert wurde und wie es geprüft wurde.
- Neue Abhängigkeiten nur mit guter Begründung und kompatibler Lizenz (MIT, Apache-2.0, BSD, OFL für Schriften);
  in `THIRD_PARTY_NOTICES.md` eintragen.
