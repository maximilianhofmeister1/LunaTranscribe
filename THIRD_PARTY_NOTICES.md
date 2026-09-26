# Drittanbieter-Komponenten

LunaTranscribe selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Die folgenden Komponenten gehören anderen und
stehen unter ihren eigenen Lizenzen.

## Im App-Bundle bzw. als Swift-Paket eingebunden

| Komponente | Zweck | Lizenz |
|---|---|---|
| [FluidAudio](https://github.com/FluidInference/FluidAudio) | ASR (Parakeet, Cohere), Sprechererkennung, Vokabular-Boosting über CoreML | Apache-2.0 |
| [mlx-swift-examples](https://github.com/ml-explore/mlx-swift-examples) (MLXLLM, MLXLMCommon, MLXEmbedders) und [mlx-swift](https://github.com/ml-explore/mlx-swift) | Lokale Sprachmodelle auf der GPU | MIT |
| [KeyboardShortcuts](https://github.com/sindresorhus/KeyboardShortcuts) | Globale Tastenkürzel | MIT |
| [MCP Swift SDK](https://github.com/modelcontextprotocol/swift-sdk) | MCP-Server `luna-mcp` (Claude-Anbindung) | Apache-2.0 (neue Beiträge) / MIT (bestehender Code) |
| [swift-system](https://github.com/apple/swift-system), [swift-log](https://github.com/apple/swift-log) | Abhängigkeiten des MCP SDK | Apache-2.0 |
| [EventSource](https://github.com/mattt/EventSource) | Abhängigkeit des MCP SDK (HTTP-Transport, von luna-mcp nicht genutzt) | MIT |
| SQLite (Teil von macOS) | Suchindex (FTS5) | Public Domain |
| [MediaRemoteAdapter](https://github.com/ungive/mediaremote-adapter) (`Vendor/`) | Wiedergabe pausieren/fortsetzen | BSD-3-Clause |
| [Fraunces](https://github.com/undercasetype/Fraunces) (`Resources/Fonts`) | Überschriften | SIL Open Font License 1.1 (`OFL-Fraunces.txt`) |
| [Figtree](https://github.com/erikdkennedy/figtree) (`Resources/Fonts`) | Text | SIL Open Font License 1.1 (`OFL-Figtree.txt`) |

Die Schriften dürfen mit der App weitergegeben, aber nicht einzeln verkauft werden; ihre Lizenztexte liegen im
App-Bundle neben den Schriftdateien.

## Beim ersten Start heruntergeladen (nicht im Repository)

Modelle werden von Hugging Face geladen und nicht mit LunaTranscribe verteilt. Wer die App nutzt, nutzt die Modelle
unter deren Lizenz:

| Modell | Verwendung | Lizenz |
|---|---|---|
| NVIDIA Parakeet TDT 0.6B v3 (CoreML via FluidAudio) | Spracherkennung (Standard) | CC-BY-4.0 |
| Cohere Transcribe 03-2026 (CoreML via FluidAudio) | Spracherkennung (optional) | Apache-2.0 |
| NVIDIA Parakeet CTC 110M (CoreML via FluidAudio) | Akustisches Vokabular-Boosting | CC-BY-4.0 |
| pyannote speaker-diarization community-1 (CoreML via FluidAudio) | Sprechererkennung | CC-BY-4.0 |
| Liquid AI LFM2 1.2B (`mlx-community/LFM2-1.2B-4bit`) | Nachbearbeitung (Standard) | LFM Open License v1.0 (Apache-2.0-basiert; kostenlose kommerzielle Nutzung nur unter 10 Mio. USD Jahresumsatz) |
| Qwen3 1.7B / 4B (`mlx-community/Qwen3-*-4bit`) | Nachbearbeitung (optional) | Apache-2.0 |
| Apple Foundation Models | Nachbearbeitung (optional, macOS 26+) | Teil von macOS |
| intfloat/multilingual-e5-small | Inhaltliche Suche für luna-mcp (optional, erst nach Einschalten) | MIT |

## Optionale externe Dienste

- **Claude Code / Claude Desktop** (Anthropic): starten `luna-mcp`, wenn der Nutzer es dort einträgt und in
  LunaTranscribe „Meetings für Claude freigeben“ einschaltet. Was Claude über luna-mcp liest, verarbeitet Anthropic
  unter seinen Bedingungen.

- **Claude CLI** (Anthropic): wird nur aufgerufen, wenn der Nutzer bei einem Meeting „Zusammenfassen“ klickt. Sie ist
  nicht Teil von LunaTranscribe, muss separat installiert werden und unterliegt den Bedingungen von Anthropic.
