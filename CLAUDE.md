# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

LunaTranscribe for Windows: the Windows port of the macOS app (github.com/maximilianhofmeister1/LunaTranscribe,
Swift/SwiftUI). Same features, same structure, same design („Mondnacht“). Tray dictation app (hotkey → record →
local transcription with Parakeet TDT v3 / Cohere Transcribe via sherpa-onnx → paste into the active app), plus
meeting mode, refine hotkey (local LLM), custom + learning vocabulary, meeting library with Claude CLI summaries,
main window (Übersicht, Meetings, Wörterbuch, Verlauf, Einstellungen) and the local MCP server `luna-mcp`.

Windows-only additions that the macOS version does not have (keep them): the model is unloaded after N minutes
without dictation (`Settings.UnloadAfterMinutes`, `ModelManager.UnloadAfter`) and reloaded in parallel to the next
recording.

**Language:** code comments, log messages, UI strings and the README are German. Identifiers stay English.

## Build & run

- .NET 8 SDK, Windows 10 2004+ x64. `dotnet build LunaTranscribe.sln` (Debug), `dotnet run --project LunaTranscribe -c Release`.
- Release install: `.\scripts\build-release.ps1` (self-contained publish to `%LOCALAPPDATA%\Programs\LunaTranscribe`).
- Unit tests only for `LunaCore`: `dotnet test LunaCore.Tests`.
- Developer self-tests replace an app test target: a static method `static Task<int> X(string[] args)` marked
  `[SelfTest("--name-selftest")]` anywhere in the app assembly runs via `LunaTranscribe.exe --name-selftest …`
  (no tray, no hook, no model preload; output via `SelfTests.Print` to console + log). Extend these instead of adding debug UI.

## Structure

- `LunaCore/` (net8.0, no UI, no native deps): AppLanguage, EngineKind, meeting model/library, vocabulary logic,
  summary prompt, search. Shared with `luna-mcp`. Anything testable without Windows UI goes here, with a test.
- `LunaTranscribe/` (WPF, net8.0-windows): App/ (AppState, self-tests), Audio/, Engine/, Hotkey/, Meeting/,
  Output/, Refine/, Settings/, UI/ (Theme/, MainWindow/, Pages/, tray, popover, overlay).
- Settings: JSON at `%APPDATA%\LunaTranscribe\settings.json`; models/queue/index/logs under `%LOCALAPPDATA%\LunaTranscribe`.

## Conventions

- UI only from `UI/Theme` tokens and styles (DynamicResource brushes `Luna.*`, fonts `Luna Display` / `Luna Title`
  / `Luna Text`), light and dark. The macOS SwiftUI views are the reference for layout and copy.
- UI-facing classes live on the dispatcher thread; background work hops back via the dispatcher before touching
  observable state.
- Invariants from macOS hold: a dictation is never lost (refine failure → raw text), a meeting recording is never
  lost until saved or discarded, a transcript is never lost (unsaved folder), vocabulary suggestions are never auto-applied,
  `luna-mcp` never deletes without a native confirmation and only to the recycle bin; confidential meetings stay invisible to Claude.
