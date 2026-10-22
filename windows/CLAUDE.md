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
- GitHub release assets: `.\scripts\package-release.ps1` → `<repo>\dist\LunaTranscribe-<version>-windows-x64-setup.exe`
  (Inno Setup 6, `installer\LunaTranscribe.iss`: per-user, no admin, same install folder, kills the running app and
  `luna-mcp` before replacing files, uninstall keeps settings/models/meetings and removes the Run key; the `AppId` GUID
  must never change) and `…-windows-x64.zip`, each with a `.sha256`. Scripts must stay UTF-8 **with BOM** (Windows
  PowerShell 5.1 reads them as ANSI otherwise).
- Unit tests only for `LunaCore` and `luna-mcp`: `dotnet test LunaCore.Tests`, `dotnet test LunaMCP.Tests`.
- `luna-mcp` has its own checks: `mcp\luna-mcp.exe --selftest` (temp sample library, every tool/resource/prompt, delete
  only with a scripted „decline“, plus an embedding sanity check if the model is present), `--check` (real setup,
  read-only; behind „Verbindung testen“), `--confirm-selftest` (shows the real dialog with a 5 s timeout). For isolated
  runs set `LUNA_DATA_DIR`, `LUNA_SETTINGS_FILE` (also honoured by the app's SettingsStore) and `--library <dir>`.
- Developer self-tests replace an app test target: a static method `static Task<int> X(string[] args)` marked
  `[SelfTest("--name-selftest")]` anywhere in the app assembly runs via `LunaTranscribe.exe --name-selftest …`
  (no tray, no hook, no model preload; output via `SelfTests.Print` to console + log). Extend these instead of adding debug UI.

## Structure

- `LunaCore/` (net8.0, no UI; only native dep is SQLite via Microsoft.Data.Sqlite): AppLanguage, EngineKind, meeting
  model/library, vocabulary logic, summary prompt, activity log + undo, search (`Search/`: `Chunker`, `KeywordQuery`,
  `HybridRanker` (RRF k=60, max 3 hits/meeting), `SearchIndex` (SQLite WAL, FTS5 `unicode61 remove_diacritics 2`, Float32
  vectors brute-force cosine, idempotent `Sync` via content hash, `EmbedPendingAsync`, cache via `PRAGMA data_version`,
  confidential meetings never indexed), `ITextEmbedder`, `EmbeddingModelKind`, `XlmRobertaTokenizer` (HF tokenizer.json:
  precompiled charsmap + Unigram, exact XLM-R ids)). Shared with `luna-mcp`. Anything testable without Windows UI goes
  here, with a test.
- `LunaMCP/` (net8.0-windows, exe `luna-mcp.exe`, self-contained, official C# MCP SDK `ModelContextProtocol.Core`, stdio):
  port of `LunaMCPServer` (tools, resources + completion, 6 prompts, `MeetingCatalog`, `ConfirmationGuard`, double opt-in,
  activity log, errors as `isError` results), `TaskDialogConfirmer` (comctl32 TaskDialog on an STA thread, topmost,
  „Abbrechen“ default, 120 s → declined), `OnnxTextEmbedder` (multilingual-e5-small ONNX on CPU via ONNX Runtime 1.31,
  E5 prefixes, mean pooling + L2, loaded offline only). Extra commands for the app: `--index`, `--download-embedding-model`.
  **stdout is JSON-RPC only** – logs go to stderr and `%LOCALAPPDATA%\LunaTranscribe\luna-mcp.log`. The app build copies
  it to `<app>\mcp\` (never into the app folder: sherpa-onnx ships a different onnxruntime.dll there).
- App side of MCP: `LunaTranscribe/Search/` (`SearchIndexer` runs `mcp\luna-mcp.exe --index` in the background while
  `McpAccessEnabled`, never while `IsBusy()`; `ClaudeConnection` copy commands + `--check`), Settings → Datenschutz
  (`UI/Pages/Settings/PrivacySettings.cs`).
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
