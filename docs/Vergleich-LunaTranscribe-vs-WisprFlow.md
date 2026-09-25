# LunaTranscribe vs. Wispr Flow – detaillierter Funktionsvergleich

Stand: 25.09.2026. LunaTranscribe-Angaben stammen aus dem Quellcode und der README dieses Repos (Commit `8588850`).
Wispr-Flow-Angaben stammen aus einer Internetrecherche (offizielle Website, Help Center, Changelog, Wikipedia,
unabhängige Reviews); Quellen stehen am Ende. Mit „WhisperFlow“ ist das Produkt **Wispr Flow** der Firma Wispr AI
gemeint. Wispr Flow ist Closed Source und ändert sich schnell – Angaben dazu sind so gut wie die jeweilige Quelle und
können bereits veraltet sein. Wo Quellen sich widersprechen, steht das dabei.

---

## 1. Kurzfassung

| | **LunaTranscribe** | **Wispr Flow** |
|---|---|---|
| Grundprinzip | Lokal, offline, Open-Weights-Modelle auf dem Mac | Cloud-Dienst, eigenes proprietäres Sprachmodell („Canto“) + Cloud-LLMs |
| Plattformen | Nur macOS 15+ auf Apple Silicon | macOS, Windows, iPhone, Android (+ Sync) |
| Preis | Kostenlos (Eigenbau) | Free (2.000 Wörter/Woche Desktop), Pro 12–15 $/Monat, Growth 18–23 $/Nutzer, Enterprise |
| Internet nötig | Nur für einmalige Modell-Downloads | Immer – ohne Verbindung keine Diktate |
| Datenschutz | Audio verlässt nie den Mac, keine Analytics | Audio + Text gehen in die US-Cloud; Training standardmäßig **an**; mehrere öffentliche Vorfälle |
| KI-Nachbearbeitung | Separater Hotkey, lokale LLMs (LFM2 / Qwen3 / Apple Intelligence), bewusst konservativ | Standardmäßig bei jedem Diktat, kontextabhängiger Ton pro App, 4 Stufen „Auto Cleanup“ |
| Sprachbefehle / Text umschreiben | Nein | Ja („Command Mode“, Pro) |
| Snippets / Textbausteine | Nein | Ja (inkl. Rich Text) |
| Wörterbuch | Ja, manuell; zwei Stufen (Alias-Ersetzung + akustisches Boosting) | Ja, lernt automatisch aus Korrekturen; Team-Wörterbuch |
| Meetings | Ja: Mikro + Systemaudio, lokale Sprechertrennung, Export TXT/MD | Ja („Notetaker“, seit Aug. 2026): bot-frei, Namen aus Kalender, KI-Zusammenfassung, Cloud |
| Sprachen | 30 (Parakeet v3: 25 europäische; Cohere: 14 inkl. CJK/Arabisch) | 100+ (104), automatische Spracherkennung |
| Team-Funktionen | Keine | Geteilte Wörterbücher/Snippets, Dashboards, SSO, SCIM, Admin-Portal |

**Fazit in einem Satz:** Wispr Flow ist das funktionsreichere, plattformübergreifende Produkt mit stärkerer KI-Umformulierung;
LunaTranscribe gewinnt klar bei Datenschutz, Offline-Fähigkeit, Kosten, Kontrolle über Modelle/Prompt und
Zuverlässigkeit ohne Netz – und hat mit dem Meeting-Modus eine vollständig lokale Alternative zum Cloud-Notetaker.
Der Grund dafür ist die **Local-First-Architektur**, ausführlich beschrieben in [Abschnitt 18](#18-lunatranscribe-ist-local-first--im-detail).

---

## 2. Hintergrund & Produktphilosophie

### LunaTranscribe
- Privates Eigenbau-Projekt, Swift/SwiftUI, Menüleisten-App.
- Leitidee: **alles lokal** – Spracherkennung per CoreML auf der Neural Engine (FluidAudio), LLM per MLX auf der GPU
  oder über Apples Systemmodell.
- Designprinzip „**ein Diktat geht nie verloren**“: Scheitert die Nachbearbeitung, wird der Rohtext eingefügt.
- Nachbearbeitung ist bewusst **nicht umformulierend** (Wortwahl, Ton, Du/Sie bleiben erhalten).

### Wispr Flow
- Firma Wispr AI, gegründet 2021 von Tanay Kothari und Sahaj Garg; ursprünglich ein Wearable für
  „stille“ Eingabe über Nervensignale, 2024 Pivot zu reiner Software.
- Finanzierung: Series A 30 Mio. $ (Menlo Ventures, 2025), Extension 25 Mio. $ (Nov. 2025),
  **Series B 280 Mio. $ bei 2 Mrd. $ Bewertung (Aug. 2026)**.
- Nutzer: ca. 40 % USA, 30 % Europa; nach eigenen Angaben >50 % monatliches Wachstum.
- Leitidee: „Voice-first“-Eingabe für alles – Diktat, Umschreiben per Sprachbefehl, Notizen, Meetings,
  Team-Produkt mit Enterprise-Verwaltung.
- Nachbearbeitung ist bewusst **umformulierend und tonangepasst** (Slack locker, E-Mail formell).

---

## 3. Plattformen, Systemvoraussetzungen, Installation

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| macOS | 15+, **nur Apple Silicon** | Ja (Intel/Apple Silicon laut allgemeinen Angaben) |
| Windows | – | Ja |
| iOS | – | Ja, als Drittanbieter-Tastatur |
| Android | – | Ja (Tastatur/„Bubble“) |
| Linux | – | – |
| Geräteübergreifende Synchronisation | – | Ja (Wörterbuch, Snippets, Scratchpad, Notizen) |
| Installation | Selbst bauen (Xcode, XcodeGen, Metal Toolchain), signieren, `scripts/build-release.sh` | Download + Account-Anmeldung |
| Account nötig | Nein | Ja |
| Speicherbedarf Download | Parakeet ~0,5 GB bzw. Cohere ~2 GB, LLM 0,7–2,3 GB, Sprecher-Modelle <100 MB, CTC-Boosting ~100 MB | Kleiner Client, Modelle in der Cloud |
| RAM | ASR + LLM dauerhaft geladen (LFM2 ~0,9 GB, Qwen3 4B ~2,8 GB zusätzlich) | Laut Reviews ~800 MB RAM und ~8 % CPU im Leerlauf, 8–10 s Startzeit |
| Erster Start | Einmaliges Warm-up ~1 min (Neural-Engine-Kompilierung) | Sofort nach Login |

**Bewertung:** Wispr Flow ist klar überlegen bei Reichweite und Einstiegshürde. LunaTranscribe ist ein Entwickler-Setup
für genau einen Mac; eine Weitergabe an Dritte bräuchte Developer ID + Notarisierung.

---

## 4. Spracherkennung (ASR)

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Modell | **Parakeet TDT v3** (NVIDIA, Standard) oder **Cohere Transcribe**, via FluidAudio/CoreML | **Canto** (seit 17.09.2026, eigenes ~2-Mrd.-Parameter-Modell); vorher Drittanbieter-Modelle |
| Ort der Ausführung | Neural Engine des Macs | Wispr-Cloud (USA) |
| Sprachen | 30 (siehe unten), Sprache fest pro Hotkey | 100+ (104), automatische Erkennung und Wechsel „on the fly“ |
| Sprachwahl | Primär- und Sekundärsprache auf zwei Hotkeys; Fallback auf Englisch, wenn Engine die Sprache nicht kann | In-App-Sprachwahl (seit März 2026), Auto-Detect, gemischte Sprachen |
| Robustheit gegen Lärm | Abhängig von Parakeet/Cohere; keine eigene Optimierung | Canto explizit auf reale Umgebungen (Zug, Großraumbüro, Wind) trainiert; laut Wispr niedrigste WER vs. Google, OpenAI, AssemblyAI, Deepgram auf 9,8 h eigenen Aufnahmen (Eigenmessung, nicht unabhängig geprüft) |
| Flüstermodus | – | „Whisper mode“ für leises Diktieren |
| Kontext für Schreibweisen | Wörterbuch (s. Abschn. 7) | „Context-aware spelling“: nutzt umgebenden Text/aktive App |
| Max. Diktatlänge | Praktisch nur durch RAM begrenzt (16 kHz mono ≈ 4 MB/min) | 20 Minuten pro Diktat (seit März 2026) |
| Streaming/Live-Text | Nein – Text nach Loslassen | Nein – Text nach Ende des Diktats |

**Sprachen LunaTranscribe:** Deutsch, Englisch, Französisch, Italienisch, Spanisch, Portugiesisch, Niederländisch,
Polnisch, Griechisch, Arabisch, Chinesisch, Japanisch, Koreanisch, Vietnamesisch (Cohere) sowie Rumänisch, Dänisch,
Schwedisch, Finnisch, Ungarisch, Estnisch, Lettisch, Litauisch, Maltesisch, Tschechisch, Slowakisch, Slowenisch,
Kroatisch, Bulgarisch, Russisch, Ukrainisch (Parakeet v3; alle europäischen Sprachen der Liste auch dort).

**Hinweis Wispr Flow & Deutsch:** Laut Changelog kamen Deutsch, Spanisch, Italienisch und Portugiesisch erst im
Juni 2026 als „zusätzliche Desktop-Sprachen“ hinzu (für bestimmte Funktionen wie Styles/Cleanup); Styles sind laut
Features-Seite weiterhin **nur Englisch/Desktop**, Command-Mode-Triggerphrasen **nur Englisch**, Notetaker
**voll nur Englisch**. Für deutschsprachige Nutzer ist die KI-Schicht von Wispr Flow damit teils eingeschränkt.

---

## 5. KI-Nachbearbeitung (Cleanup / Refine)

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Aktivierung | **Eigener dritter Hotkey** („Diktieren + Nachbearbeitung“); normale Hotkeys liefern Rohtext | Standardmäßig bei jedem Diktat; „Auto Cleanup“ in 4 Stufen (seit April 2026) |
| Modelle | Lokal: **LFM2 1.2B** (Standard), **Qwen3 1.7B**, **Qwen3 4B** (MLX, 4-bit, GPU) oder **Apple Intelligence** (macOS 26+) | Cloud-LLMs (nicht offengelegt) |
| Füllwörter entfernen | Ja („äh“, „halt“, „sozusagen“) | Ja |
| Grammatik/Zeichensetzung | Ja | Ja, zusätzlich Zeichensetzung aus Pausen/Tonfall |
| Selbstkorrekturen („um 2 … nein 3“) | Innerhalb eines Satzes ab Qwen3 4B zuverlässig; ganze wiederholte Sätze nicht | Ja, beworben als Kernfunktion („Let's meet at 2… actually 3“) |
| Listen aus gesprochenen Zahlen | Nein | Ja (nummerierte Listen) |
| Ton/Stil anpassen | **Nein, absichtlich** – Wortwahl, Ton, Du/Sie bleiben | Ja: „Styles“ pro App-Kategorie (Very Casual … Formal), „Personalized Style“ 2026; nur Englisch/Desktop |
| Kontext der Ziel-App | Nein | Ja („Context Awareness“, liest aktive App; früher auch Screenshots, s. Abschn. 11) |
| Prompt editierbar | **Ja**, vollständiger System-Prompt in den Einstellungen, `{language}`-Platzhalter, Reset | Nein (nur Stufen/Styles) |
| Geschwindigkeit | LFM2 ~0,6–0,8 s, Qwen3 1.7B ~1 s, Qwen3 4B ~2–2,5 s, Apple ~1 s (M3) | Netzwerkabhängig; Wispr meldet −30 % Latenz seit Jahresbeginn 2026 |
| Timeout/Fallback | 20 s Timeout; bei Fehler, Timeout, unplausibler Länge (außerhalb 0,4–2×) → Rohtext + Hinweis im Overlay | Bei Ausfall kein Diktat möglich (Cloud) |
| Rohtext nachvollziehbar | Ja, Tooltip im Verlauf, Einträge mit ✨ markiert | Lokaler Verlauf in der App |
| Wörterbuch nach LLM | Ja, wird erneut angewendet, damit das LLM Begriffe nicht „zurückkorrigiert“ | Nicht dokumentiert |

**Bewertung:** Wispr Flow bietet die „magischere“ Nachbearbeitung (Ton, Listen, Kontext), aber mit weniger
Kontrolle und primär auf Englisch optimiert. LunaTranscribe ist vorhersagbarer, transparent (Prompt sichtbar) und
schnell, stößt aber bei komplexen Selbstkorrekturen an die Grenzen kleiner lokaler Modelle.

---

## 6. Sprachbefehle, Umschreiben, Snippets, Notizen

| Funktion | LunaTranscribe | Wispr Flow |
|---|---|---|
| **Command Mode** – markierten Text per Sprache umschreiben („mach das kürzer“, „übersetze ins Spanische“, „mach Stichpunkte daraus“) | – | Ja, Pro-Funktion; Hotkey Fn+Ctrl (Mac) bzw. Ctrl+Win+Alt; Triggerphrasen nur Englisch |
| Websuche per Sprache („ask/search/hey …“ → Google, Perplexity, ChatGPT, Claude) | – | Ja, über Command Mode |
| **Transforms** (KI-Umschreib-Vorlagen) | – | Beta seit Mai 2026 |
| **Snippets** (Sprachkürzel → Textbausteine, z. B. Kalenderlink) | – | Ja, seit Juli 2026 mit Fett/Kursiv/Links/Listen; im Team teilbar |
| **Scratchpad** (schwebender Notizblock mit Tabs + Sync) | – | Beta seit Mai 2026 |
| Diktierte Formatierbefehle („neuer Absatz“) | Nein | Ja (manuelle Zeichensetzung/Befehle) |
| Integration in Claude/ChatGPT | – | Laut Preisseite ja |

**Bewertung:** Hier liegt der größte funktionale Abstand. LunaTranscribe ist ein reines Diktier-Werkzeug;
Wispr Flow ist eine Sprach-Eingabeschicht mit Editier- und Makrofunktionen.

---

## 7. Eigenes Wörterbuch / Vokabular

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Pflege | Manuell: Begriff + optionale Varianten („LunaTranscribe ← Luna Transcribe, Luna transkribe“) | Manuell **und automatisch** (lernt aus Korrekturen); Einträge markieren/ranken (seit März 2026) |
| Stufe 1: Text-Ersetzung | Vorkompilierter Regex, case-insensitiv, Unicode-Wortgrenzen, längste Variante zuerst; jede Engine/Sprache | Nicht im Detail dokumentiert |
| Stufe 2: Akustisches Boosting | Parakeet CTC 110M rescored das Audio und findet auch nicht eingetragene Fehlschreibungen („Weglap“ → „weclapp“); ~0,15 s pro Diktat | „Context-aware spelling“ in der Cloud |
| Gilt für Meetings | Ja | Ja (Wörterbuch fließt in Notetaker-Transkripte) |
| Team-Wörterbuch | – | Ja, auch abteilungsübergreifend (seit Sept. 2026) |
| Entwickler-Vokabular | Über eigene Einträge | Eingebaut (Supabase, Vercel …), camelCase/snake_case, CLI-Befehle |
| Messbar/testbar | `--vocab-selftest` mit Vorher/Nachher-Vergleich | – |

**Bewertung:** Wispr Flow ist bequemer (Auto-Lernen, Teams). LunaTranscribes Zwei-Stufen-Ansatz ist technisch
transparent, deterministisch und reproduzierbar testbar; bewusste Abweichungen (Spotter-Rescue aus) verhindern
Fehltreffer in deutschen Sätzen.

---

## 8. Bedienung, Hotkeys, Einfügen

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Standard-Hotkey | F2 | Fn (Mac), Ctrl+Win (Windows); ohne Fn-Taste Ctrl+Opt |
| Modi | „Gedrückt halten“ oder „Ein/Aus umschalten“ (global einstellbar) | Push-to-Talk; Doppeltipp → Freihandmodus; Fn+Space = Freihand |
| Anzahl Diktat-Hotkeys | 3: Primärsprache, Sekundärsprache, Primärsprache + Nachbearbeitung | Diktat, Freihand, Command Mode |
| Maus als Auslöser | – | „Mouse Flow“ für Zusatz-Maustasten (seit März 2026) |
| Abbrechen | Escape oder X im Overlay – verwirft Aufnahme **und** laufende Transkription | Dreifachtipp auf Shortcut |
| Einfügen | Pasteboard + synthetisches ⌘V (Bedienungshilfen-Recht) | Systemweite Texteingabe; mobil als Tastatur |
| Visuelles Feedback | Schwebendes Overlay: „Aufnahme… → Transkribiere… → Formuliere…“ | „Flow Bar“/Bubble |
| Medienwiedergabe pausieren | **Ja**: pausiert Musik/Video beim Aufnahmestart, setzt nur selbst pausierte Wiedergabe fort (MediaRemoteAdapter) | Nicht dokumentiert |
| Mikrofonwahl | Ja (Einstellungen → Audio) | Ja, inkl. virtueller Geräte (Krisp, NVIDIA Broadcast, BlackHole; seit Aug. 2026) |
| Verlauf | „Letzte Diktate“: max. 20, **nur im RAM**, abschaltbar, Rohtext im Tooltip | Lokaler Verlauf (normal / 24 h / aus), optional Cloud-Sync |
| Autostart | Ja (`SMAppService`) | Ja |
| Bekannte Probleme | Hotkey F2 braucht ggf. Systemeinstellung „F-Tasten als Standard“ | Secure Keyboard Entry kann Shortcuts blockieren; Bericht über systemweiten Tastatur-Interceptor, der Leertasten verschluckte (April 2026) |

---

## 9. Meetings

| Aspekt | LunaTranscribe – Meeting-Modus | Wispr Flow – Notetaker |
|---|---|---|
| Verfügbar seit | Commit `5574c5f` | 05.08.2026 (Mac), Windows seit 15.09.2026 |
| Aufnahme | Mikrofon + **Systemaudio** per Core-Audio-Prozess-Tap + privates Aggregate-Device; kein Bot | Mikrofon + Systemaudio lokal aufgenommen; kein Bot im Call |
| Unterstützte Apps | Alles, was über den Mac läuft (Zoom, Teams, Browser …) | Zoom, Meet, Teams, Slack Huddles, Discord, Browser, Präsenz |
| Verarbeitung | **Lokal** nach Meeting-Ende: Diarization (pyannote community-1, CoreML) → Turns → Transkription pro Turn | **Cloud**; zweiter Durchlauf zur Genauigkeitsprüfung |
| Sprecher | „Sprecher 1…n“, im Ergebnisfenster umbenennbar; Teilnehmerzahl vorgebbar (erzwingt Clustering) | Namen automatisch aus Kalender, Gmail, Slack erschlossen + Verifikationsmodell; Korrektur per Klick |
| Zusammenfassung / Action Items | – | Ja: Zusammenfassung nach Themen + nächste Schritte pro Person; Vorbereitung vor dem Meeting |
| Export | Text / Markdown, Kopieren; automatisches `.txt`-Backup im Ablageordner; WAV optional behalten | In-App, Cloud-Sync; Import von Otter/Granola/Fathom; Suche via MCP |
| Kalender | – | Google + Outlook (seit Sept. 2026) |
| Sprachen | Alle Engine-Sprachen (30) | 21 Sprachen (Sept. 2026), voll unterstützt primär Englisch |
| Limits | Keine | Free: wöchentliche Obergrenze; Pro höher; Growth unbegrenzt |
| Parallel diktieren | Ja, Diktate landen auch im Meeting-Audio | Nicht dokumentiert |
| Dauer der Verarbeitung | 1 h ≈ 1–2 min (Parakeet), ≈ 5 min (Cohere) | Cloudabhängig |
| Einwilligung/Compliance | Nutzerverantwortung | Admin-Steuerung für Consent-Management (Enterprise) |
| Selbsttest | `--meeting-selftest` | – |

**Bewertung:** Wispr Flows Notetaker ist komfortabler (echte Namen, Zusammenfassungen, Kalender). LunaTranscribe
ist die datenschutzfreundliche Variante: Das komplette Gespräch – inklusive Stimmen Dritter, die nicht eingewilligt
haben – verlässt nie den Rechner. Eine lokale KI-Zusammenfassung wäre mit dem vorhandenen Refine-Stack naheliegend.

---

## 10. Team-, Business- und Analysefunktionen

| Funktion | LunaTranscribe | Wispr Flow |
|---|---|---|
| Team-Workspace, zentrale Abrechnung | – | Pro |
| Geteilte Wörterbücher/Snippets | – | Pro |
| Insights-Dashboard (Wörter, Top-Apps, Kommunikationsprofil) | – | Seit April 2026 |
| Leaderboards | – | Pro-Teams (Juli 2026) |
| SSO/SAML, HIPAA mit BAA, mehrere Domains | – | Growth |
| SCIM, Audit-Logs, MDM, Domain Capture, gesperrtes Modelltraining | – | Enterprise |
| Tägliche Teammate-Digest-Mails für Admins | – | Seit Aug. 2026 |

LunaTranscribe ist ein Einzelplatz-Werkzeug; das ist hier kein Mangel, sondern ein anderes Einsatzziel.

---

## 11. Datenschutz & Sicherheit

### LunaTranscribe
- Diktat-Audio nur im RAM, direkt nach der Transkription verworfen.
- Meeting-Audio temporär in `~/Library/Caches/LunaTranscribe/Meetings`, danach gelöscht (außer „Aufnahme behalten“).
- LLM-Nachbearbeitung lokal; Wörterbuch in UserDefaults; Verlauf nur im RAM.
- **Keine Cloud, keine Analytics, kein Account.** Einzige Netzverbindung: einmalige Modell-Downloads von Hugging Face.
- Kein App Sandbox (nötig für globale Hotkeys + ⌘V), aber Hardened Runtime; Code ist vollständig einsehbar.

### Wispr Flow
- Cloud-Verarbeitung in den **USA**; Verschlüsselung in Transit und at Rest, aber nicht Ende-zu-Ende.
- Zwei getrennte Schalter: **„Improve the model for everyone“** (Training, **standardmäßig an**) und
  **Dictation Cloud Storage**. Keine Datenhaltung nur mit Training aus **und** Cloud-Speicher aus.
- Lokaler Verlauf wählbar: normal / Löschen nach 24 h / gar nicht.
- Zertifizierungen: SOC 2 Type II, ISO 27001, HIPAA (laut Wispr). Einschränkung: Die früheren Berichte stammen
  vom Compliance-Anbieter Delve, der im März 2026 wegen Boilerplate-Berichten in die Kritik geriet; im April 2026
  folgte ein neuer SOC 2 Type I von A-LIGN.
- Dokumentierte Vorfälle (laut unabhängigen Reviews):
  1. 2025: Aktive-Fenster-**Screenshots** wurden für Context Awareness hochgeladen; der meldende Nutzer wurde
     gesperrt, später Entschuldigung des CTO und Überarbeitung.
  2. April 2026: Forensik-Bericht zu Mac-Version 1.4.752 – systemweiter Tastatur-Interceptor, lokale Datenbank mit
     3.404 Diktaten und 198 MB Roh-Audio, stündliche Uploads trotz deaktiviertem Datenteilen.
  3. Mai/Juni 2026: mehrtägige Ausfälle – ohne lokale Rückfalloption auf allen Plattformen.
  4. Juni/August 2026: öffentliche Auswertungen von Diktatdaten (Nutzungsanalysen pro Firma, Wortfrequenzen nach Land).
- DSGVO: Datenverarbeitung in den USA → für EU-Firmen AVV/Transfer-Grundlage nötig.

**Bewertung:** Deutlichster Unterschied zugunsten von LunaTranscribe. Für vertrauliche Inhalte (Kundendaten,
Gesundheit, Recht, interne Meetings) ist ein lokales System strukturell im Vorteil – nicht nur durch Richtlinien,
sondern durch die Architektur.

---

## 12. Zuverlässigkeit, Leistung, Offline

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Offline | **Voll funktionsfähig** | **Nicht nutzbar** (Mikrofon-Button grau im Flugmodus) |
| Abhängigkeit von Dritten | Keine zur Laufzeit | Wispr-Infrastruktur + Vendoren; Statusseite mit Latenzvorfällen |
| Verfügbarkeit | Lokal = so zuverlässig wie der Mac | 99,9 % laut Wispr (Juli 2026); Nutzerberichte über Hänger/Timeouts |
| Latenz Diktat | Lokal, unabhängig von Netz; ~0,15 s Zusatz für Vokabular-Boosting | Netzabhängig; schnell bei guter Verbindung |
| Hardwarelast | Modelle dauerhaft im RAM (je nach LLM 1–3+ GB) | ~800 MB RAM/8 % CPU im Leerlauf (Review-Angabe) |
| Update-Kanal | Manuell (selbst bauen) | Automatisch, mit Update-Planung ohne Unterbrechung (Sept. 2026) |

---

## 13. Kosten

| Plan | LunaTranscribe | Wispr Flow |
|---|---|---|
| Kostenlos | Vollständig, unbegrenzt | 2.000 Wörter/Woche (Desktop), 1.000 (iPhone); Notetaker begrenzt; kein Command Mode |
| Einzelperson unbegrenzt | 0 € | Pro 15 $/Monat bzw. 12 $/Monat jährlich (≈ 144 $/Jahr); 50 % Rabatt für Studierende/Lehrende |
| Team mit Admin-Kontrollen | – | Growth 18–23 $/Nutzer/Monat |
| Enterprise | – | Individuell, nur jährlich |
| Versteckte Kosten | Entwicklungszeit, Wartung, Hardware (Apple Silicon, RAM) | Abo läuft dauerhaft |

---

## 14. Transparenz & Anpassbarkeit

| Aspekt | LunaTranscribe | Wispr Flow |
|---|---|---|
| Quellcode | Vollständig eigener Code | Closed Source |
| Modellwahl | 2 ASR-Engines, 4 LLM-Backends frei wählbar | Keine Wahl |
| Prompt | Frei editierbar | Nicht einsehbar |
| Erweiterbarkeit | Neue Backends per Protokoll (`TextRefiner`/`RefinerKind`) ohne UI-Änderungen | Nur über Produkt-Roadmap |
| Reproduzierbare Tests | `--refine-selftest`, `--vocab-selftest`, `--meeting-selftest` | – |
| API | – | Keine öffentliche Canto-API |

---

## 15. Funktionslücken von LunaTranscribe gegenüber Wispr Flow (priorisiert)

Sortiert nach geschätztem Nutzen im Verhältnis zum Aufwand im bestehenden Code.

1. **Command Mode (markierten Text per Sprache bearbeiten)** – hoher Nutzen. Mit dem vorhandenen Refine-Stack machbar:
   Auswahl per ⌘C lesen, Anweisung diktieren, LLM mit eigenem Prompt, Ergebnis per ⌘V ersetzen. Qwen3 4B nötig für
   brauchbare Qualität; „Dictation never lost“-Prinzip analog (Original bleibt bei Fehler).
2. **Snippets / Textbausteine** – geringer Aufwand, deterministisch: Auslöserphrase → Text, analog zu
   `CustomVocabulary` als Stufe nach der Transkription.
3. **Meeting-Zusammenfassung + Action Items** – lokal mit Qwen3 4B über den Turn-Text; bei langen Meetings Chunking nötig.
4. **Freihandmodus per Doppeltipp** zusätzlich zu Halten/Umschalten.
5. **Automatische Spracherkennung** statt fester Sprache pro Hotkey (Parakeet v3 erkennt Sprache teils selbst; wäre zu prüfen).
6. **Wörterbuch lernt aus Korrekturen** – schwer ohne Überwachung der Ziel-App; datenschutzfreundlich höchstens als
   Vorschlag aus dem Verlauf.
7. **Stil pro App** (Ziel-App über `NSWorkspace.frontmostApplication` ermitteln, Prompt-Variante wählen) – günstig
   machbar, widerspricht aber dem bisherigen Grundsatz „nicht umformulieren“; sollte optional bleiben.
8. **Listen/Formatierbefehle** („neuer Absatz“, „Punkt eins …“) – per Regeln oder Prompt.
9. Weitere Plattformen (Windows/iOS) – außerhalb des Projektrahmens.

## 16. Alleinstellungsmerkmale von LunaTranscribe gegenüber Wispr Flow

- 100 % offline und lokal, kein Account, keine Telemetrie.
- Keine Wortlimits, keine Kosten.
- Meeting-Transkription komplett lokal inkl. Sprechertrennung und vorgebbarer Teilnehmerzahl.
- Freie Wahl von ASR-Engine und LLM, editierbarer Prompt, Apple-Intelligence-Option.
- Zweistufiges, testbares Wörterbuch mit akustischem Boosting.
- Garantiertes Sicherheitsnetz: Rohtext bei jedem Nachbearbeitungsfehler, auch bei Ausfall (es gibt keinen Server,
  der ausfallen kann).
- Pausieren/Fortsetzen laufender Medienwiedergabe.
- Zweite Sprache auf eigenem Hotkey – gezielter als Auto-Erkennung bei ähnlichen Sprachen.
- Stark für Deutsch und 25 europäische Sprachen, wo Wispr Flows KI-Funktionen (Styles, Command-Triggers, Notetaker)
  teils nur Englisch unterstützen.

---

## 17. Empfehlung nach Anwendungsfall

| Anwendungsfall | Besser geeignet |
|---|---|
| Vertrauliche Inhalte, DSGVO-sensible Umgebung, Meetings mit Externen | **LunaTranscribe** |
| Arbeiten ohne/mit schlechtem Internet (Zug, Flugzeug) | **LunaTranscribe** |
| Deutsch/europäische Sprachen, ein Mac | **LunaTranscribe** (Wispr Flow gleichwertig bei reiner Erkennung, schwächer bei KI-Extras) |
| Viele Plattformen (Windows, Smartphone), Sync | **Wispr Flow** |
| Texte per Sprache umschreiben, Snippets, Ton pro App | **Wispr Flow** |
| Meeting-Notizen mit echten Namen und Zusammenfassung | **Wispr Flow** |
| Teams mit Admin-, SSO-, Analytics-Anforderungen | **Wispr Flow** |
| Kostenloses, unbegrenztes Diktieren | **LunaTranscribe** |

---

## 18. LunaTranscribe ist Local-First – im Detail

Der wichtigste Unterschied zu Wispr Flow ist kein einzelnes Feature, sondern die Architektur. Wispr Flow ist
**Cloud-First**: Der Mac ist Mikrofon und Tastatur, Erkennung und KI sitzen auf Servern in den USA. LunaTranscribe
ist **Local-First**: Rechenleistung, Modelle und Daten liegen auf dem eigenen Mac. Das Netz braucht die App nur
einmal, um Modelle zu laden. Dieser Abschnitt erklärt, was das konkret heißt, wie man es prüfen kann und wo die
ehrlichen Grenzen liegen.

### 18.1 Was „Local-First“ bedeutet

Der Begriff stammt aus dem Essay *Local-first software* (Ink & Switch, 2019). Er nennt sieben Ideale. So erfüllt
LunaTranscribe sie im Vergleich zu Wispr Flow:

| Ideal | LunaTranscribe | Wispr Flow |
|---|---|---|
| **1. Schnell** – keine Wartezeit auf Server | ✅ Erkennung auf der Neural Engine, LLM auf der GPU; keine Netzwerk-Rundreise | ⚠️ Jede Anfrage geht über das Netz; Latenz hängt von der Verbindung ab |
| **2. Mehrere Geräte** | ❌ Nur ein Mac, keine Synchronisation | ✅ Mac, Windows, iOS, Android mit Sync |
| **3. Offline** | ✅ Voll funktionsfähig ohne Internet | ❌ Ohne Verbindung keine Diktate |
| **4. Zusammenarbeit** | ❌ Einzelplatz | ✅ Team-Wörterbücher, Snippets, Dashboards |
| **5. Langlebigkeit** – funktioniert auch, wenn der Anbieter verschwindet | ✅ Modelle liegen lokal, Code ist eigener Code; kein Server, der abgeschaltet werden kann | ❌ Ohne Wispr-Server und Account ist die App wertlos |
| **6. Privatsphäre** | ✅ Audio und Text verlassen den Mac nicht | ❌ Audio und Text gehen in die US-Cloud; Training standardmäßig an |
| **7. Kontrolle durch den Nutzer** | ✅ Modelle, Prompt, Wörterbuch, Ablageorte frei wählbar; Daten in offenen Formaten | ❌ Geschlossenes System, Einstellungen nur im Rahmen des Produkts |

LunaTranscribe erfüllt fünf von sieben Idealen voll. Die zwei offenen Punkte (mehrere Geräte, Zusammenarbeit) sind
bewusste Grenzen einer Einzelplatz-App, keine Architekturfehler.

### 18.2 Datenfluss: Diktat

```
Mikrofon ─► AudioRecorder (16 kHz mono, nur RAM)
              │
              ▼
        TranscriptionEngine (Parakeet/Cohere, CoreML, Neural Engine)
              │   + Wörterbuch Stufe 1 (Regex) und Stufe 2 (CTC-Boosting, lokal)
              ▼
        [optional] TextRefiner (LFM2/Qwen3 via MLX auf der GPU oder Apple Foundation Models on-device)
              │   + Wörterbuch erneut
              ▼
        TextInjector: Zwischenablage ─► ⌘V in die aktive App ─► alte Zwischenablage zurück
              │
              ▼
        Audio-Samples verlassen den Scope und sind weg. Text bleibt nur im RAM-Verlauf (max. 20, abschaltbar).
```

An keiner Stelle gibt es einen Netzwerkaufruf. Auch **Apple Intelligence** läuft hier lokal: Das Framework
`FoundationModels` nutzt ausschließlich Apples On-Device-Modell, nicht Private Cloud Compute.

### 18.3 Datenfluss: Meeting

```
Mikrofon + Systemaudio (Core-Audio-Prozess-Tap, privates Aggregate-Device)
              │
              ▼
        Temporäre WAV unter ~/Library/Caches/LunaTranscribe/Meetings (nur während Aufnahme + Verarbeitung)
              │
              ▼
        SpeakerDiarizer (pyannote via CoreML, lokal) ─► Turns ─► TranscriptionEngine pro Turn (lokal)
              │
              ▼
        Transkript im Ablageordner (Standard ~/Documents/LunaTranscribe Meetings)
        WAV wird gelöscht – außer „Aufnahme behalten“ ist aktiv
```

Das gilt auch für die **Stimmen Dritter**: Wer mit LunaTranscribe ein Gespräch aufnimmt, schickt die Stimmen der
anderen Teilnehmer an niemanden weiter. Bei einem Cloud-Notetaker verlassen fremde Stimmen den Rechner – ohne dass
diese Personen dem Anbieter zugestimmt haben.

### 18.4 Wo welche Daten liegen

| Daten | Ort | Lebensdauer | Format |
|---|---|---|---|
| Diktat-Audio | Nur Arbeitsspeicher | Bis zum Ende der Transkription | Float32-Samples |
| Diktat-Text (Verlauf) | Nur Arbeitsspeicher | Bis „Löschen“ oder App-Ende; max. 20 Einträge; abschaltbar | – |
| Meeting-Audio | `~/Library/Caches/LunaTranscribe/Meetings` | Bis zum Ende der Verarbeitung; verwaiste Reste werden beim Start gelöscht | WAV |
| Meeting-Audio (behalten) | Ablageordner | Bis der Nutzer löscht | WAV |
| Meeting-Transkript + Zusammenfassung | Frei wählbarer Meeting-Ordner (Standard `~/Documents/LunaTranscribe Meetings`) | Bis der Nutzer löscht | `meeting.json` + `summary.md`; Export als TXT/MD |
| Einstellungen, Wörterbuch, gelernte Vorschläge, Prompts | `UserDefaults` (`~/Library/Preferences/com.maximhofmeister.LunaTranscribe.plist`) | Dauerhaft | plist, Wörterbuch als JSON |
| ASR-, Sprecher- und LLM-Modelle | `~/Library/Application Support/LunaTranscribe/Models` | Dauerhaft | CoreML / MLX-safetensors |
| CTC-Boosting-Modell | `~/Library/Application Support/FluidAudio/Models` | Dauerhaft | CoreML |
| Logs | macOS Unified Log (`os.Logger`) | Systemverwaltet | Keine Diktatinhalte, nur Zeiten und Zustände |

Zum Vergleich Wispr Flow: Audio, Transkripte und Bearbeitungen werden – je nach Einstellung – auf Servern in den USA
gespeichert. Ein Forensik-Bericht (April 2026) fand zusätzlich eine lokale Datenbank mit mehreren Tausend Diktaten
und Roh-Audio.

### 18.5 Alle Netzwerkverbindungen

Ohne ausdrücklichen Klick auf „Zusammenfassen“ baut LunaTranscribe genau eine Art von Verbindung auf: **HTTPS-Downloads
von Hugging Face** (`huggingface.co`) für

- das ASR-Modell (Parakeet ~0,5 GB oder Cohere ~2 GB),
- die Sprecher-Modelle (< 100 MB, beim ersten Meeting),
- das Sprachmodell für die Nachbearbeitung (LFM2 ~0,7 GB, Qwen3 1–2,3 GB),
- das CTC-Boosting-Modell (~100 MB, beim ersten Wörterbuch-Eintrag mit Parakeet).

Dabei wird nur der Modellname angefragt; es werden keine Nutzerdaten, kein Audio und kein Text übertragen. Es gibt
keinen Account, keine Telemetrie, keine Absturzberichte, keine Update-Prüfung und keine Lizenzprüfung. Sind die
Modelle geladen, braucht die App das Netz überhaupt nicht mehr.

### 18.6 Selbst nachprüfen

Local-First ist nur dann glaubwürdig, wenn man es prüfen kann:

1. **Flugmodus-Test:** WLAN aus, Kabel ab → diktieren, nachbearbeiten, Meeting aufnehmen. Alles funktioniert.
2. **Verbindungen live ansehen:** Während eines Diktats im Terminal ausführen – es sollte nichts erscheinen:

   ```bash
   nettop -p LunaTranscribe -L 0 -P
   ```

   oder `lsof -i -a -c LunaTranscribe`.
3. **Firewall:** Mit Little Snitch oder LuLu alle Verbindungen der App blockieren. Nach dem Modell-Download ändert
   das nichts an der Funktion.
4. **Quellcode:** Alle Netzwerkzugriffe laufen über `HubApi` (MLX) und die FluidAudio-Downloader.
   `grep -rn "URLSession\|HubApi" LunaTranscribe/` zeigt die einzige Stelle im eigenen Code.

Bei Wispr Flow ist keine dieser Prüfungen sinnvoll möglich: Die App funktioniert ohne Verbindung nicht, und was auf
dem Server passiert, lässt sich von außen nicht prüfen.

### 18.7 Datenschutz & Recht (DSGVO)

- **Kein Auftragsverarbeiter:** Da keine Daten an Dritte gehen, braucht es keinen Auftragsverarbeitungsvertrag (AVV)
  und keine Grundlage für eine Übermittlung in ein Drittland (Art. 44 ff. DSGVO). Bei Wispr Flow (Server in den USA)
  ist beides für Firmen Pflicht.
- **Privacy by Design (Art. 25 DSGVO):** Datensparsamkeit ist in der Architektur verankert, nicht in einer
  Einstellung. Es gibt keinen Schalter, den man vergessen kann – anders als bei Wispr Flow, wo erst die Kombination
  „Training aus“ + „Cloud-Speicher aus“ eine Speicherung verhindert.
- **Berufsgeheimnisträger** (Anwälte, Ärzte, Steuerberater): Diktate mit Mandanten- oder Patientendaten verlassen
  das Gerät nicht. Das ist mit einer Cloud-Diktier-App deutlich schwerer zu rechtfertigen.
- **Meetings:** Die Pflicht, Teilnehmer über die Aufnahme zu informieren, bleibt bestehen (§ 201 StGB – Vertraulichkeit
  des Wortes). Local-First löst die Frage „Wer bekommt die Daten?“, nicht die Frage „Darf ich aufnehmen?“.

### 18.8 Zuverlässigkeit als Folge von Local-First

- **Keine Ausfälle durch Dritte:** Wispr Flow hatte im Mai/Juni 2026 mehrtägige Störungen ohne lokale Ausweichmöglichkeit.
  LunaTranscribe hat keinen Server, der ausfallen kann.
- **Keine Änderungen von außen:** Modelle und Prompt ändern sich nur, wenn der Nutzer es will. Bei Cloud-Diensten kann
  ein Modellwechsel über Nacht das Verhalten ändern (bei Wispr z. B. der Umstieg auf Canto im September 2026).
- **„Ein Diktat geht nie verloren“:** Schlägt die Nachbearbeitung fehl, wird der Rohtext eingefügt. Das geht nur, weil
  auch die Erkennung lokal läuft – bei einem Cloud-Ausfall gäbe es gar keinen Rohtext.

### 18.9 Der Preis von Local-First

Ehrlich betrachtet hat der Ansatz Kosten:

| Kosten | Ausmaß |
|---|---|
| Speicherplatz | ~1,3 GB (Parakeet + LFM2 + Sprecher + CTC) bis ~4,5 GB (Cohere + Qwen3 4B) |
| Arbeitsspeicher | ASR und LLM bleiben geladen, damit der Hotkey sofort reagiert: ~1,5 GB bis ~4 GB |
| Hardware | Nur Apple Silicon; ältere Macs und Windows bleiben außen vor |
| Modellgröße begrenzt Qualität | Lokale 1–4B-Modelle lösen z. B. keine ganzen wiederholten Sätze auf; Wispr nutzt große Cloud-Modelle |
| Energie | Die Rechenlast liegt auf dem eigenen Akku (bei kurzen Diktaten vernachlässigbar, bei 1 h Meeting-Verarbeitung spürbar) |
| Kein Sync | Wörterbuch und Einstellungen gelten nur für diesen Mac |

### 18.10 Grenzen und Restrisiken

Local-First heißt nicht „ohne Risiko“. Diese Punkte bleiben:

- **Vertrauen in die Modellquelle:** Die Modelle kommen von Hugging Face. Ein manipuliertes Modell könnte falsch
  transkribieren, aber kein Netz öffnen: CoreML- und MLX-Gewichte sind Daten, kein ausführbarer Code.
- **Zwischenablage:** Zum Einfügen liegt der Text ca. 0,4 s in der Zwischenablage. Clipboard-Manager (z. B. Raycast,
  Paste) können ihn in dieser Zeit speichern. Wer das nicht will, schließt LunaTranscribe dort aus.
- **Kein App-Sandbox:** Nötig für globale Hotkeys und ⌘V in andere Apps. Hardened Runtime ist aktiv, die Berechtigungen
  (Mikrofon, Bedienungshilfen, Systemaudio) verwaltet macOS.
- **Unverschlüsselte Dateien:** Meeting-Transkripte und behaltene WAVs liegen als normale Dateien auf der Platte.
  Schutz im Ruhezustand bietet FileVault.
- **Die eine Cloud-Funktion ist freiwillig:** Seit v0.2 kann ein Meeting über die Claude CLI zusammengefasst werden –
  nur auf Klick, mit einmaligem Hinweis, dass der Text über das Claude-Konto an Anthropic geht. Ohne Klick verlässt nichts
  den Mac; die Transkription bleibt immer lokal.

### 18.11 Fazit Local-First

Wispr Flow ist ein Dienst, den man mietet. LunaTranscribe ist ein Werkzeug, das man besitzt. Dafür nimmt man
Einschränkungen bei Plattformen, Team-Funktionen und KI-Qualität in Kauf. Man bekommt Unabhängigkeit vom Netz und vom
Anbieter, keine laufenden Kosten und eine Privatsphäre, die in der Architektur steckt und nicht von einer Einstellung
abhängt.

---

## 19. Stand v0.2: Welche Lücken sind geschlossen?

Nach diesem Vergleich wurde LunaTranscribe auf Version 0.2 gebracht. Abgleich mit der Liste aus Abschnitt 15:

| Lücke (Abschnitt 15) | Stand v0.2 |
|---|---|
| Command Mode | offen |
| Snippets / Textbausteine | offen |
| Meeting-Zusammenfassung + Action Items | **geschlossen** – per Claude CLI, auf Klick, mit editierbarem Prompt (Kurzfassung, Themen, Entscheidungen, Aufgaben, offene Fragen) |
| Freihandmodus per Doppeltipp | offen (Umschalt-Modus vorhanden) |
| Automatische Spracherkennung | offen |
| Wörterbuch lernt aus Korrekturen | **geschlossen** – Korrekturen im Verlauf und in Meetings werden zu Vorschlägen; aktiv erst nach Bestätigung (Wispr übernimmt automatisch) |
| Stil pro App | offen (bewusst: Nachbearbeitung formuliert nicht um) |
| Listen/Formatierbefehle | offen |
| Weitere Plattformen | außerhalb des Projektrahmens |

Zusätzlich neu in v0.2, näher am Komfort von Wispr Flow:

- **Hauptfenster** mit Übersicht (heutige Wörter, Modellstatus, Tastenkürzel), Meetings, Wörterbuch, Verlauf und
  aufgeräumten Einstellungen – geöffnet über das Menüleisten-Icon, frei skalierbar.
- **Meeting-Bibliothek** statt einzelner TXT-Dateien: JSON pro Meeting im frei wählbaren Ordner, Suche, Umbenennen,
  Korrigieren, Export als TXT/MD, direkt von der Claude CLI im Terminal lesbar.
- **Eigene CI „Mondnacht“**: warmes, verspieltes Design mit Fraunces + Figtree (Wispr nutzt EB Garamond + Figtree),
  eigenes App-Icon, Hell- und Dunkelmodus, neues Overlay mit lebendiger Wellenform.
- **Open Source (MIT), kostenlos** – gegenüber Wispr Flow Pro (12–15 $/Monat).

Unterschied im Meeting-Vergleich (Abschnitt 9) nach v0.2: Wispr erschließt Sprechernamen automatisch aus Kalender, Gmail und
Slack. LunaTranscribe lässt sie von Hand benennen und bleibt dafür bei Transkription und Sprechertrennung komplett lokal.
Die Zusammenfassung ist bei beiden eine Cloud-Leistung – bei LunaTranscribe aber nur auf Klick und über das eigene Claude-Konto.

---

## Quellen

- Wispr Flow – Features: https://wisprflow.ai/features
- Wispr Flow – What's new (Changelog): https://wisprflow.ai/whats-new
- Wispr Flow – Pricing: https://wisprflow.ai/pricing
- Wispr Flow – Canto: https://wisprflow.ai/canto
- Wispr Flow – Notetaker: https://wisprflow.ai/notetaker
- Wispr Flow Help Center – Command Mode: https://docs.wisprflow.ai/articles/4816967992-how-to-use-command-mode
- Wispr Flow Help Center – Hands-free: https://docs.wisprflow.ai/articles/6391241694-use-flow-hands-free
- Wispr Flow Help Center – Shortcuts: https://docs.wisprflow.ai/articles/2612050838-supported-unsupported-keyboard-hotkey-shortcuts
- Wispr Flow Help Center – Datenschutz/Cloud Sync: https://docs.wisprflow.ai/articles/4709791908-understanding-privacy-mode-and-cloud-sync
- Wispr Flow Help Center – Security FAQ: https://docs.wisprflow.ai/articles/3467817258-security-and-compliance-faq
- Wikipedia – Wispr Flow: https://en.wikipedia.org/wiki/Wispr_Flow
- SiliconANGLE – Series B: https://siliconangle.com/2026/08/17/wispr-raises-280m-to-power-up-natural-speech-to-text-using-ai/
- Reworked – Canto & Bewertung: https://www.reworked.co/ai-platforms/wispr-hits-2b-valuation-launches-canto-voice-model/
- Voibe – Sicherheitsvorfälle: https://www.getvoibe.com/resources/is-wispr-flow-safe/
- Voibe – Zuverlässigkeit: https://www.getvoibe.com/resources/is-wispr-flow-reliable/
- LumeVoice – Offline-Frage: https://lumevoice.com/blog/does-wispr-flow-work-offline-2026/
- Zack Proser – Review: https://zackproser.com/blog/wisprflow-review
- Build Fast with AI – Review 2026: https://www.buildfastwithai.com/blogs/wispr-flow-review-2026-pricing-alternatives
- Data Studios – Notetaker/MCP: https://www.datastudios.org/post/wispr-flow-notetaker-bot-free-mac-meeting-ai-with-mcp-support-explained
- Wispr Flow Notetaker Review (Kai): https://hirekai.ai/blog/wispr-flow-notetaker
- Ink & Switch – Local-first software (2019): https://www.inkandswitch.com/local-first/

Hinweis: Mehrere der unabhängigen Quellen (Voibe, LumeVoice, Spokenly u. a.) sind selbst Anbieter konkurrierender
Diktier-Apps und daher nicht neutral. Kritische Angaben zu Vorfällen und Ressourcenverbrauch sind entsprechend
einzuordnen; die Kernaussagen (Cloud-Pflicht, Standard-Einstellung für Training, Preise) sind durch Wispr-eigene
Seiten gedeckt.
