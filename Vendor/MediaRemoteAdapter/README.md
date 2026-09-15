# MediaRemoteAdapter (vendored)

Quelle: https://github.com/ungive/mediaremote-adapter (BSD-3-Clause, siehe LICENSE)
Stand: Commit vom 4. Sep 2026, gebaut mit `scripts/build-mediaremote-adapter.sh` (arm64).

Zweck: Seit macOS 15.4 dürfen normale Apps Apples MediaRemote-Framework nicht mehr nutzen
(kein „spielt gerade etwas?“, kein Pause/Play). Der Adapter wird von `/usr/bin/perl`
(Bundle-ID `com.apple.perl5`, damit berechtigt) geladen und liefert den Zustand als JSON.

LunaTranscribe nutzt ihn ausschließlich, um laufende Wiedergabe während der Aufnahme
zu pausieren und danach fortzusetzen (`Output/MediaPlaybackController.swift`).
