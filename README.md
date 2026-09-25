# LunaTranscribe

Lokale Diktier-App für die macOS-Menüleiste. Hotkey drücken → sprechen → Text wird in die aktive App eingefügt.
Läuft komplett offline auf Apple Silicon (CoreML / Neural Engine) mit **Cohere Transcribe** oder **Parakeet TDT v3**.
Dazu ein **Meeting-Modus**: Gespräch aufnehmen (Mikrofon + Systemaudio), danach Sprecher erkennen und als Text/Markdown exportieren.
Und ein dritter Hotkey mit **Nachbearbeitung**: Ein lokales Sprachmodell (LFM2, Qwen3 oder Apple Intelligence) bereinigt
das Diktat – Rechtschreibung, Grammatik, Füllwörter, Wiederholungen, Versprecher – bevor es eingefügt wird.
Ein **eigenes Wörterbuch** sorgt dafür, dass Namen und Fachbegriffe richtig geschrieben werden – und **lernt aus
deinen Korrekturen**. Meetings landen als JSON in einer **Meeting-Bibliothek** in einem frei wählbaren Ordner und lassen
sich auf Knopfdruck über die **Claude CLI zusammenfassen** (die einzige, freiwillige Ausnahme vom Local-First-Prinzip).

Alles lebt in der Menüleiste; ein **Hauptfenster** (Übersicht, Meetings, Wörterbuch, Verlauf, Einstellungen) öffnest du
über das Menüleisten-Icon. Design „Mondnacht“: warm und verspielt, Schriften Fraunces + Figtree, Hell- und Dunkelmodus.

**Open Source (MIT), kostenlos.** Einen ausführlichen Vergleich mit Wispr Flow und die Local-First-Architektur beschreibt
[docs/Vergleich-LunaTranscribe-vs-WisprFlow.md](docs/Vergleich-LunaTranscribe-vs-WisprFlow.md).

## Voraussetzungen

- macOS 15+, Apple Silicon
- Xcode 16+ (getestet mit 27.0) inkl. **Metal Toolchain** (seit Xcode 26 separat: Xcode → Settings → Components,
  oder `xcodebuild -downloadComponent MetalToolchain`) – nötig, weil MLX seine GPU-Kernel beim Build kompiliert
- `brew install xcodegen` (nur nötig, wenn du `project.yml` änderst)

## Einrichten

1. `xcodegen generate` (nur nötig, wenn `LunaTranscribe.xcodeproj` fehlt oder `project.yml` geändert wurde)
2. `LunaTranscribe.xcodeproj` in Xcode öffnen
3. **Signatur (empfohlen):** `cp Config/Local.xcconfig.example Config/Local.xcconfig` und dort deine Team-ID
   eintragen (`security find-identity -v -p codesigning` zeigt sie). Ein kostenloses Apple-ID-„Personal Team“ reicht.
   Die Datei ist gitignored. Ohne sie wird ad-hoc signiert – das baut, aber macOS fragt dann nach jedem Build
   erneut nach Mikrofon, Bedienungshilfen und Systemaudio.
4. ⌘R

## Release-Build & Installation (für den Alltag)

```bash
./scripts/build-release.sh
```

Baut Release, signiert mit dem Team aus `Config/Local.xcconfig`, installiert nach
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
  Abschaltbar unter Einstellungen → Diktat → Audio. Technik: `Vendor/MediaRemoteAdapter` (BSD-3), siehe dortige README.
- Zweiter Hotkey mit eigener Sprache: Einstellungen → Diktat.
- **Hauptfenster**: Menüleiste → „Öffnen“ (oder das Zahnrad für die Einstellungen). Seiten: Übersicht (heutige Wörter,
  Modellstatus, Tastenkürzel, letzte Meetings), Meetings, Wörterbuch, Verlauf, Einstellungen. Das Fenster ist frei
  skalierbar und merkt sich Größe und Position. Kein Dock-Icon – die App bleibt eine Menüleisten-App.
- **Overlay**: eine Pille in Nachtblau am unteren Bildschirmrand – „Ich höre zu…“ mit lebendiger Wellenform,
  „Transkribiere…“, „Formuliere…“; sie nimmt nie den Fokus.

## Diktat mit Nachbearbeitung

Dritter Hotkey (Einstellungen → Diktat → „Diktieren + Nachbearbeitung“, kein Default): gleiches Diktat in der
Hauptsprache, aber vor dem Einfügen bereinigt ein lokales Sprachmodell den Text – wie die Kernfunktion von Superwhisper.
Das Overlay zeigt „Ich höre zu… → Transkribiere… → Formuliere…“.

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

## Eigenes Wörterbuch

Hauptfenster → **Wörterbuch**: Begriffe, die das Modell regelmäßig falsch schreibt, mit der richtigen Schreibweise
eintragen – optional mit den Varianten, als die sie erkannt werden („LunaTranscribe“ ← „Luna Transcribe, Luna transkribe“).
Gilt für **Diktat und Meeting**, nach der Nachbearbeitung wird es erneut angewendet (das Sprachmodell kann einen Begriff
also nicht „zurückkorrigieren“). Der Schalter „Eigenes Wörterbuch verwenden“ schaltet alles ab; die Einträge bleiben erhalten.

Zwei Stufen, beide aus derselben Liste:

1. **Varianten ersetzen** (jede Engine, jede Sprache): Jede eingetragene Variante und der Begriff in falscher
   Groß-/Kleinschreibung werden durch die richtige Schreibweise ersetzt – nur an Wortgrenzen, unabhängig von
   Groß-/Kleinschreibung und Leerzeichen. Deterministisch, kostet nichts.
2. **Akustisches Boosting** (nur Parakeet): FluidAudios CTC-Vocabulary-Boosting lässt ein zweites, kleines Modell
   (Parakeet CTC 110M, einmaliger Download ~100 MB) über das Audio laufen und ersetzt ein erkanntes Wort nur, wenn das
   Audio stärker nach dem Wörterbuch-Begriff klingt. Findet so auch Fehlschreibungen, die nicht als Variante eingetragen sind
   („Weglap“, „WegLab“ → „weclapp“). Kostet ~0,15 s pro Diktat.

Selbsttest (Release-Build, M3, Sprachausgabe „Anna“/„Samantha“, sechs Sätze, sechs Begriffe):

| Satz (gesprochen) | ohne Wörterbuch | mit Wörterbuch |
|---|---|---|
| „… heute LunaTranscribe mit FluidAudio getestet …“ | Luna Transkribe, Fluid Audio | LunaTranscribe, FluidAudio |
| „… das Grafana Dashboard in Weclapp.“ | WegLab. | weclapp. |
| „… den Export aus Weclapp schicken …“ | Weglap | weclapp |
| „Das Wetter ist heute schön …“, „Der Graf kam nach Hause, wir klappen …“ | unverändert | unverändert (keine Fehltreffer) |

Hinweise: Das CTC-Modell ist auf **Englisch** trainiert – es wirkt am besten bei Namen, Marken und Fachbegriffen ab
vier Buchstaben, auch in deutschen Sätzen. Für normale deutsche Wörter lieber Varianten eintragen. FluidAudios
„Spotter-Rescue“ (Vorschläge allein aus dem Audio, ohne Ähnlichkeit zum erkannten Wort) ist bewusst **aus**: Im
Selbsttest machte er aus „Das Wetter ist heute schön“ „weclapp weclapp“. Das CTC-Modell liegt – anders als die übrigen
Modelle – unter `~/Library/Application Support/FluidAudio/Models`, weil FluidAudio seinen Tokenizer nur dort sucht.

Wörterbuch ohne Mikrofon testen – transkribiert jede Datei ohne Wörterbuch, nur mit Boosting und mit beiden Stufen:

```bash
say -v Anna -o /tmp/luna.wav --data-format=LEF32@16000 "Ich teste LunaTranscribe mit Kubernetes"
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --vocab-selftest /tmp/luna.wav --terms "LunaTranscribe=Luna Transcribe;Kubernetes" --language de
```

Ohne `--terms` nimmt der Test das Wörterbuch aus den Einstellungen.

### Lernendes Wörterbuch

Korrigierst du ein Wort im **Verlauf** (Stift-Knopf) oder in einem **Meeting-Transkript**, vergleicht LunaTranscribe
vorher und nachher Wort für Wort und schlägt passende Einträge vor („Weglap“ → „weclapp“). Die Vorschläge erscheinen
oben auf der Seite **Wörterbuch** (mit Zähler in der Seitenleiste): **Übernehmen** (neuer Begriff oder zusätzliche
Variante eines bestehenden), Begriff vorher anpassen, oder **Ignorieren** (kommt nicht wieder). Nichts wird
automatisch aktiv – eine falsche Ersetzung wäre in jedem künftigen Diktat, eine fehlende nur ein Tippfehler.

Als Verhörer zählt eine Ersetzung von 1–3 Wörtern, deren Schreibweise oder Klang ähnlich ist. Verworfen werden
Umformulierungen (weniger als die Hälfte der Wörter blieb gleich, oder zu unähnlich), getauschte Funktionswörter
(„die“ → „der“), Großschreibung am Satzanfang, reine Zeichensetzung und Beugungen normaler Wörter („klappen“ →
„klappten“). Großschreibung mitten im Satz gilt dagegen als Marke („Iphone“ → „iPhone“).

```bash
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --learn-selftest                       # eingebaute Fälle
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --learn-selftest "vorher" "nachher"    # eigener Fall
```

## Meeting-Modus

Menüleiste → **Meeting starten**. Während des Meetings wird nur aufgenommen (16 kHz mono in eine
temporäre WAV-Datei, kaum CPU-Last) – **Diktieren per Hotkey funktioniert währenddessen ganz normal**
und landet ebenfalls im Meeting-Transkript. Escape wirkt nur auf das Diktat; ein Meeting wird
ausschließlich über das Menü beendet oder verworfen.

**Meeting beenden** reiht die Aufnahme in die **Hintergrund-Verarbeitung** ein – du kannst **sofort das nächste
Meeting starten**. Die Verarbeitung läuft Meeting für Meeting nacheinander (Fortschritt im Menü unter „Im Hintergrund“,
in der Seitenleiste und oben auf der Seite Meetings):

1. Sprecher-Erkennung (Diarization) mit FluidAudios Offline-Pipeline (pyannote community-1, CoreML).
2. Zusammenhängende Redebeiträge (Turns) werden gebildet und einzeln mit der gewählten Engine transkribiert.
3. Das Meeting wird in der **Meeting-Bibliothek** gespeichert; eine **macOS-Mitteilung** meldet es, ein Klick öffnet es.

Die Warteschlange liegt unter `~/Library/Application Support/LunaTranscribe/Queue` (Aufnahme + kleine Auftragsdatei mit
Sprache und Personenzahl). **Beendest du die App mit wartenden Meetings, geht es beim nächsten Start weiter.** Schlägt eine
Verarbeitung fehl, bleibt die Aufnahme erhalten: im Menü „Erneut versuchen“ oder „Verwerfen“. Ein Meeting lässt sich auch
starten, während die Modelle noch laden – die Verarbeitung wartet dann einfach.

Richtwerte: 1 h Meeting ≈ 1–2 min Verarbeitung mit Parakeet, ≈ 5 min mit Cohere – im Hintergrund, parallel zu Diktat
und neuer Aufnahme. Beim allerersten Mal
kommt die einmalige Kompilierung der Sprecher-Modelle dazu.

**Systemaudio** (Einstellungen → Meetings, Standard an): Für Online-Meetings wird die Wiedergabe von Zoom, Teams,
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

### Meeting-Bibliothek

Jedes Meeting ist ein Ordner im **Meeting-Ordner** (Standard `~/Documents/LunaTranscribe Meetings`, frei wählbar unter
Einstellungen → Meetings):

```
2026-09-25 14-03 Planung Q4 · a1b2c3/
  meeting.json   Transkript, Sprecher, Titel, Zusammenfassung (Quelle der Wahrheit, versioniert)
  summary.md     Zusammenfassung als Markdown – sobald vorhanden
  audio.wav      nur mit „Aufnahme behalten“
```

Die Seite **Meetings** zeigt die Bibliothek nach Monaten gruppiert, mit Volltextsuche. Im Meeting: Titel umbenennen,
Sprecher benennen, einzelne Beiträge korrigieren (speist das lernende Wörterbuch) – alles wird automatisch gespeichert.
**TXT/Markdown gibt es nur noch als Export** (mit oder ohne Zusammenfassung), dazu Kopieren, „Im Finder zeigen“ und
Löschen (Papierkorb).

- **Ordner wechseln**: Die App fragt, ob vorhandene Meetings mitziehen sollen.
- **Ordner nicht erreichbar** (externes Laufwerk ab): Neue Meetings landen vorübergehend in
  `~/Library/Application Support/LunaTranscribe/Unsaved` und ziehen beim nächsten Einlesen automatisch um – ein
  Transkript geht nie verloren.
- **Alte TXT-Backups** aus Version 0.1 („Meeting 2026-09-15 14-03.txt“) werden beim Start einmalig importiert;
  die Originale bleiben unangetastet.

### Zusammenfassung mit Claude

Im Meeting auf **Zusammenfassen** klicken: LunaTranscribe ruft die lokal installierte **Claude CLI** auf
(`claude -p`, ohne Werkzeuge, ohne MCP, ohne Projektkontext, ohne gespeicherte Sitzung) und legt Kurzfassung,
Themen, Entscheidungen, Aufgaben und offene Fragen im Tab „Zusammenfassung“ und als `summary.md` ab. Dauer je nach
Länge 10–60 s.

- **Voraussetzung**: Claude CLI installiert und angemeldet (`curl -fsSL https://claude.ai/install.sh | bash`, dann
  einmal `claude` starten). Die App sucht sie in `~/.local/bin`, Homebrew und der Login-Shell; ein eigener Pfad und
  ein Modell (z. B. `sonnet`) lassen sich unter Einstellungen → Meetings setzen, „Testen“ prüft Pfad und Version.
- **Anweisung**: Einstellungen → Meetings → „Anweisung für die Zusammenfassung“, frei editierbar (`{language}` = Sprache
  des Meetings), „Auf Standard zurücksetzen“ stellt die mitgelieferte Fassung wieder her.
- **Local-First bleibt ehrlich**: Die Transkription ist lokal. Für die Zusammenfassung geht der Text des Meetings über
  dein Claude-Konto an Anthropic. Beim ersten Mal erklärt ein Hinweis das; der Button trägt ein Wolken-Symbol.

Ohne App testen (gleicher Code wie der Button; Achtung, der Text geht an Anthropic):

```bash
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --summary-selftest "<Meeting-Ordner>/meeting.json" [--model sonnet] [--cli /pfad/zu/claude]
```

### Meetings im Terminal mit Claude

Weil die Bibliothek aus normalen Ordnern mit JSON und Markdown besteht, kann Claude Code sie direkt lesen:

```bash
cd ~/Documents/"LunaTranscribe Meetings" && claude "Fasse alle Meetings dieser Woche zusammen und liste offene Aufgaben je Person"
```

Geplant ist ein eigener, lokal laufender **MCP-Server** (`luna-mcp`), über den auch Claude Desktop die Meetings inhaltlich
durchsuchen und lesen, Sprecher benennen, Meetings umbenennen und – nur nach deiner Bestätigung – löschen kann; mit
Freigabe-Schaltern, vertraulichen Meetings und Protokoll. Plan und Stand:
[docs/MCP-Server-Plan.md](docs/MCP-Server-Plan.md).

## Datenschutz

- Diktat-Audio bleibt nur im RAM (16 kHz mono ≈ 4 MB/min) und wird direkt nach der Transkription verworfen.
- Meeting-Audio liegt während Aufnahme und Verarbeitung temporär unter `~/Library/Caches/LunaTranscribe/Meetings`
  und wird danach gelöscht – außer „Aufnahme behalten“ ist aktiv, dann wandert die WAV in den Meeting-Ordner.
  Transkripte liegen als `meeting.json` im Meeting-Ordner.
- Der Rohtext eines Diktats mit Nachbearbeitung geht nur in das lokale Sprachmodell auf diesem Mac.
- Keine Analytics, kein Account. Netzwerk nur für den einmaligen Modell-Download (ASR + Sprecher-Modelle + Sprachmodell +
  Wörterbuch-Zusatzmodell) – und, **nur auf Klick**, für eine Meeting-Zusammenfassung über die Claude CLI (Text an Anthropic).
- Das Wörterbuch und die gelernten Vorschläge liegen in den App-Einstellungen (UserDefaults), nicht in der Cloud.
- „Letzte Diktate“ (Verlauf) leben nur im Arbeitsspeicher (abschaltbar unter Einstellungen → Datenschutz).
- Die Übersicht zählt „Wörter/Diktate heute“ über einen eigenen Tageszähler in den Einstellungen – nur Datum und zwei
  Zahlen, kein Text. Er zählt auch mit ausgeschaltetem Verlauf und beginnt jeden Tag bei null.

## Struktur

```
LunaTranscribe/
  App/        LunaTranscribeApp (MenuBarExtra, Self-Test-Einstieg), AppState (Wurzel: verdrahtet alles),
              DictationController (Aufnahme → Engine → Refine → Einfügen), Log, AppPaths
  Audio/      AudioRecorder (16 kHz Float32, Sink RAM oder WAV-Datei), AudioDevices, DeviceInputStream,
              SystemAudioTap (Prozess-Tap + Aggregate-Device für Systemaudio)
  Engine/     TranscriptionEngine-Protokoll, CohereEngine, ParakeetEngine, ModelManager
  Hotkey/     KeyboardShortcuts-Namen (Default F2), EscapeKeyMonitor
  Meeting/    MeetingSession (Statusmaschine), MeetingTranscriber (Diarizer → Turns → Engine), SpeakerDiarizer,
              MeetingTranscript (Codable, Export), MeetingStore (Bibliothek), MeetingDocument (geöffnetes Meeting,
              Autosave), MeetingExporter, LegacyMeetingImporter, MeetingSelfTest
  Output/     TextInjector (Pasteboard + ⌘V), Permissions, MediaPlaybackController (Wiedergabe pausieren)
  Refine/     TextRefiner-Protokoll + RefinerKind, RefinePrompt, MLXRefiner, AppleIntelligenceRefiner, RefinerManager
  Summary/    ClaudeCLI (Prozessaufruf), MeetingSummarizer, SummaryPrompt, SummarySelfTest
  Settings/   SettingsStore, AppLanguage, Pages/ (eine Datei pro Einstellungsbereich)
  UI/         Theme/ (Farben, Schriften, Bausteine), MainWindow/ (Fenster, Navigation, Seitenleiste), Pages/ (Übersicht,
              Meetings, Wörterbuch, Verlauf), MenuBarView, OverlayController, UISnapshotSelfTest
  Vocabulary/ CustomVocabulary, CorrectionLearner (Wort-Diff), VocabularyLearner (Vorschläge), Self-Tests
  Resources/  Fonts (Fraunces, Figtree – OFL), Assets.xcassets (App-Icon, Akzentfarbe)
Config/       Signing.xcconfig (+ lokale Local.xcconfig mit Team-ID)
scripts/      build-release.sh, make-icon.swift (zeichnet das App-Icon), build-mediaremote-adapter.sh
Vendor/MediaRemoteAdapter/   Framework + Perl-Skript für Now-Playing-Zugriff (macOS ≥ 15.4)
```

Design neu rendern, ohne zu klicken (alle Seiten + Popover, hell und dunkel, mit Beispiel-Meeting):

```bash
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --ui-snapshot /tmp/luna-ui
```

Das App-Icon wird per Code gezeichnet: `swift scripts/make-icon.swift` (Vorschau: `--preview /tmp/icon.png`).

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

Hintergrund-Verarbeitung ohne Mikrofon testen: reiht jede Datei als beendetes Meeting ein (wie mehrere Meetings direkt
hintereinander), wartet auf alle und druckt Reihenfolge und Ergebnis; gespeichert wird in einen temporären Ordner:

```bash
/Applications/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --queue-selftest /tmp/a.wav /tmp/b.wav
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

## Lizenz

LunaTranscribe steht unter der [MIT-Lizenz](LICENSE). Mitgelieferte und genutzte Fremdkomponenten (Swift-Pakete,
Schriften, MediaRemoteAdapter) und die Lizenzen der heruntergeladenen Modelle stehen in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Mitmachen: [CONTRIBUTING.md](CONTRIBUTING.md).
