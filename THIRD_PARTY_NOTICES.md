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

## Windows-Version (`windows/`)

Im Release-ZIP enthalten bzw. als NuGet-Paket eingebunden:

| Komponente | Zweck | Lizenz |
|---|---|---|
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (`org.k2fsa.sherpa.onnx`) | ASR (Parakeet, Cohere) und Sprachabschnitte für die Sprechererkennung | Apache-2.0 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (mit sherpa-onnx; `Microsoft.ML.OnnxRuntime` für luna-mcp) | Inferenz auf der CPU | MIT |
| [NAudio](https://github.com/naudio/NAudio) | Mikrofon- und Systemaudio-Aufnahme (WASAPI), Resampling | MIT |
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) mit [llama.cpp](https://github.com/ggml-org/llama.cpp) | Lokale Sprachmodelle (Nachbearbeitung) auf der CPU | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | Suchindex (SQLite FTS5) | MIT / Apache-2.0 |
| [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol.Core`) | MCP-Server `luna-mcp` | Apache-2.0 |
| .NET Runtime (self-contained) | Laufzeitumgebung | MIT |
| Fraunces, Figtree (`windows/LunaTranscribe/Assets/Fonts`) | Schriften als statische Instanzen, umbenannt in „Luna Display/Title/Text“ (OFL-konform) | SIL Open Font License 1.1 |

Beim ersten Start heruntergeladen (zusätzlich bzw. statt der macOS-Modelle):

| Modell | Verwendung | Lizenz |
|---|---|---|
| Parakeet TDT 0.6B v3 INT8 (`csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`) | Spracherkennung (Standard) | CC-BY-4.0 |
| Cohere Transcribe INT8 (`csukuangfj2/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01`) | Spracherkennung (optional) | Apache-2.0 |
| pyannote segmentation 3.0 (`csukuangfj/sherpa-onnx-pyannote-segmentation-3-0`) | Sprechererkennung (Sprachabschnitte) | MIT |
| 3D-Speaker CAM++ (`csukuangfj/speaker-embedding-models`) | Sprechererkennung (Stimm-Embeddings) | Apache-2.0 |
| LFM2 1.2B GGUF (`LiquidAI/LFM2-1.2B-GGUF`) | Nachbearbeitung (Standard) | LFM Open License v1.0 |
| Qwen3 1.7B / 4B GGUF (`unsloth/Qwen3-1.7B-GGUF`, `Qwen/Qwen3-4B-GGUF`) | Nachbearbeitung (optional) | Apache-2.0 |
| intfloat/multilingual-e5-small (ONNX) | Inhaltliche Suche für luna-mcp (optional) | MIT |

## Optionale externe Dienste

- **Claude Code / Claude Desktop** (Anthropic): starten `luna-mcp`, wenn der Nutzer es dort einträgt und in
  LunaTranscribe „Meetings für Claude freigeben“ einschaltet. Was Claude über luna-mcp liest, verarbeitet Anthropic
  unter seinen Bedingungen.

- **Claude CLI** (Anthropic): wird nur aufgerufen, wenn der Nutzer bei einem Meeting „Zusammenfassen“ klickt. Sie ist
  nicht Teil von LunaTranscribe, muss separat installiert werden und unterliegt den Bedingungen von Anthropic.
