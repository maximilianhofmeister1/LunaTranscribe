# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

LunaTranscribe is a macOS menu-bar dictation app (Swift/SwiftUI, macOS 15+, Apple Silicon only). Hotkey → record → local CoreML transcription (FluidAudio: Parakeet TDT v3 or Cohere Transcribe) → text pasted into the frontmost app. A third hotkey adds **LLM post-processing** (`Refine/`: LFM2/Qwen3 via MLX, or Apple Foundation Models) that cleans grammar, fillers and repetitions before pasting. A separate **meeting mode** records mic + system audio to a WAV, then diarizes (pyannote via FluidAudio) and transcribes per speaker turn. Fully offline except the one-time model downloads. Details for users (permissions, first start, privacy) are in `README.md`.

**Language:** code comments, log messages, UI strings and the README are German. Keep new comments and user-facing strings German; identifiers stay English.

## Build & run

The Xcode project is **generated from `project.yml` with XcodeGen** and is gitignored (`*.xcodeproj`). Edit `project.yml`, never the `.pbxproj`, and regenerate after changes:

```bash
xcodegen generate
```

Debug build from the CLI (Xcode: open `LunaTranscribe.xcodeproj`, ⌘R):

```bash
xcodebuild -project LunaTranscribe.xcodeproj -scheme LunaTranscribe -configuration Debug -destination 'platform=macOS,arch=arm64' -derivedDataPath build/debug build 2>&1 | grep -E "error:|warning: .*LunaTranscribe/LunaTranscribe/|BUILD"
```

Release build, sign, install to `/Applications`, and launch (the everyday path; the scheme's Run config is Release because FluidAudio is much slower in Debug):

```bash
./scripts/build-release.sh
```

Code signing uses the fixed `DEVELOPMENT_TEAM` in `project.yml` so TCC permissions (mic, accessibility, system audio) survive rebuilds. There is no App Sandbox (needed for global hotkeys + ⌘V injection) and hardened runtime is on.

Building needs the **Metal Toolchain** (separate Xcode component since Xcode 26; `xcodebuild -downloadComponent MetalToolchain`) because mlx-swift compiles its GPU kernels. The first build after a clean takes several minutes for that. `FoundationModels` is weak-linked (`OTHER_LDFLAGS` in `project.yml`) so the macOS 15 deployment target still launches; every use of that framework sits behind `#available(macOS 26, *)`.

**There are no unit tests.** The only automated check is the meeting-pipeline self-test, which records N seconds of mic + system audio, processes it, and writes channel levels + transcript to a log, then exits. It must be launched via `open` (not directly from a terminal), otherwise TCC attributes the system-audio permission to the terminal and the tap records silence:

```bash
open -n /Applications/LunaTranscribe.app --args --meeting-selftest 20 --log /tmp/luna-selftest.log
```

Play something (e.g. `say -v Anna "…"`) while it runs. Implemented in `Meeting/MeetingSelfTest.swift`, triggered from `LunaTranscribeApp.init`.

The refine pipeline has its own self-test that needs no audio permission, so it can run straight from the terminal (first run downloads the model). Use it to iterate on `RefinePrompt`; it prints one cold and two warm passes with timings:

```bash
./build/debug/Build/Products/Debug/LunaTranscribe.app/Contents/MacOS/LunaTranscribe --refine-selftest "ähm also ich wollte sagen dass dass wir morgen um elf uhr das meeting haben" --model qwen3_1_7b --language de
```

While either self-test runs, `AppState(preloadModels: false)` skips loading models so two downloads don't race on the same file.

Logs go through `os.Logger` with subsystem `com.maximhofmeister.LunaTranscribe` (categories `timing`, `meeting`); read with `log stream --predicate 'subsystem == "com.maximhofmeister.LunaTranscribe"'`.

`scripts/build-mediaremote-adapter.sh` rebuilds the vendored `Vendor/MediaRemoteAdapter` framework from upstream (needs cmake); only needed when bumping that dependency.

## Architecture

Two independent state machines share one loaded model:

- **`AppState`** (`App/`, `@MainActor ObservableObject`) — dictation. Owns `ModelManager`, `RefinerManager`, `AudioRecorder` (memory sink), `OverlayController`, `MediaPlaybackController`, and the `MeetingSession`. Flow: `KeyboardShortcuts` hotkey (`Hotkey/Shortcuts.swift`; each `HotkeySlot` maps to a `DictationRequest` = language + `refine` flag; `.refined` uses the primary language) → `startRecording` → `stopRecordingAndTranscribe` → `engine.transcribe(samples, language:)` → optional `refine` (status `.refining`, overlay „Formuliere…") → `TextInjector.insert` (pasteboard + synthetic ⌘V, requires Accessibility). A `transcriptionGeneration` counter discards late results after Escape/cancel, checked again after refining. `Transcript.rawText` keeps the pre-refine text for the history tooltip. `AppState` re-publishes `MeetingSession` and `RefinerManager` `objectWillChange` so the menu bar only observes `AppState`.
- **`MeetingSession`** (`Meeting/`) — meeting mode. Own `AudioRecorder` (file sink → temp WAV in `~/Library/Caches/LunaTranscribe/Meetings`), `SystemAudioTap`, and a long-lived `SpeakerDiarizer`. Recording does nothing but write audio; all processing happens in `stop()` via `MeetingTranscriber.run` on a detached task, reporting `MeetingProgress` back to `status`. Escape deliberately does **not** cancel a meeting; only the menu does. Dictation keeps working during a meeting and lands in the meeting audio too.

**Refine pipeline** (`Refine/`): mirrors `Engine/` deliberately — `TextRefiner` protocol + `RefinerKind` enum + `RefinerManager` (same shape as `ModelManager`, kept separate instead of a generic manager). Backends: `MLXRefiner` (LFM2 1.2B default, Qwen3 1.7B, Qwen3 4B; `LLMModelFactory.loadContainer` with `HubApi(downloadBase: ModelPaths.base)`, greedy, `enable_thinking: false`, `maxTokens` derived from input length, 20 s timeout) and `AppleIntelligenceRefiner` (`@available(macOS 26, *)`, new `LanguageModelSession` per call). `RefinerKind.availability` asks `SystemLanguageModel.default.availability` and is what greys out the Apple option in Settings. `RefinePrompt` is the backend-neutral part: `defaultTemplate` (English system prompt with a `{language}` placeholder; the user can edit it in Settings, stored in `SettingsStore.refinePrompt` only when it differs from the default), the dictation wrapped in `<dictation>` tags (stops small models answering questions in the text), few-shot `examples` sent as real user/assistant turns (as prose in the system prompt, 1B models copy the example format), and `sanitize` which strips tags/preambles/quotes and rejects outputs outside 0.4–2× the input length. Iterate on prompts with `--refine-selftest … --prompt <file>` instead of rebuilding. Known limit (tested 2026-09-21 on all three MLX models with several prompt variants): a whole repeated-and-corrected sentence („Heute ist … nein, morgen ist …") is not resolved; in-sentence corrections („Montag nein Dienstag") work from Qwen3 4B up. Adding more rules about it made results worse — don't spend more time there without a bigger model. **Invariant: a dictation is never lost** — `AppState.refine` returns the raw transcript plus a notice on any failure (model still loading, error, timeout, implausible output) and the caller pastes that.

**Engines** (`Engine/`): `TranscriptionEngine` protocol (`prepare(progress:)`, `transcribe(_:language:)`, `unload()`) with `ParakeetEngine` and `CohereEngine` wrapping FluidAudio. `ModelManager` keeps exactly one engine loaded and swaps on `SettingsStore.engine` changes. Models live under `ModelPaths.base` (`~/Library/Application Support/LunaTranscribe/Models`), shared with the diarizer models. `AppLanguage` maps each language to the FluidAudio enums of both engines; `SettingsStore.languageForEngine` falls back to English when the chosen language is unsupported by the active engine.

**Meeting pipeline** (`MeetingTranscriber`): diarizer segments → `buildTurns` (merge same-speaker segments with gaps ≤ 1 s, fold turns < 0.5 s into the predecessor) → slice each turn from the WAV with 0.15 s padding → `engine.transcribe` per turn → renumber speakers as „Sprecher 1…n" by first appearance. This **turn-slicing** design is intentional: it keeps the engine protocol unchanged and works with Cohere, which returns no word timestamps. Thresholds are constants at the top of `MeetingTranscriber`. `MeetingSpeakerCount` (0 = auto) is passed to the diarizer clustering because automatic clustering tends to collapse similar voices into one speaker.

**Audio capture** (`Audio/`): `AudioRecorder` produces 16 kHz mono Float32 from a `CaptureSource` (device + `channelGroups` for per-source mixing, e.g. `[[mic], [sysL, sysR]]`). System audio uses a Core Audio process tap plus a private aggregate device (output device as master/clock + mic + tap) read via a raw IOProc in `DeviceInputStream` — not `AVAudioEngine`, which drops the channel layout after device changes. `tapautostart` is intentionally off so a silent recording still produces a file. If the system-audio permission is denied, macOS silently delivers silence; `MeetingSession.captureSource` falls back to mic-only and sets `systemAudioActive = false` for the menu hint. Input/output devices are fixed at meeting start.

**Media pause** (`Output/MediaPlaybackController`): macOS ≥ 15.4 blocks MediaRemote for normal apps, so the vendored `MediaRemoteAdapter.framework` (embedded, not linked, code-signed) is loaded by `/usr/bin/perl` via `mediaremote-adapter.pl` (bundled as a resource). Only resumes playback it paused itself.

**Settings** (`Settings/SettingsStore`): singleton over `UserDefaults`; each `@Published` property persists in `didSet`. `launchAtLogin` is backed by `SMAppService`, not defaults.

**Adding a refine backend:** implement `TextRefiner`, add a `RefinerKind` case (label, `huggingFaceID` or `nil`, availability), and map it in `RefinerManager.make`. Settings and AppState need no changes.

## Conventions

- Swift concurrency with `SWIFT_STRICT_CONCURRENCY: minimal`; UI-facing classes are `@MainActor`, progress callbacks are `@Sendable` and may arrive on any thread — hop to the main actor before touching published state.
- Everything user-facing lives in the menu-bar popover (`UI/MenuBarView`), the floating overlay (`UI/OverlayController`), the Settings scene (`Settings/SettingsView`) and the meeting result window (`UI/MeetingResultView`). Meeting failures are additionally shown as an `NSAlert` because the popover is usually closed by then.
- Dependencies are SwiftPM packages declared in `project.yml` (FluidAudio, KeyboardShortcuts, mlx-swift-examples → products `MLXLLM` + `MLXLMCommon`); `Package.resolved` under the xcodeproj is regenerated, not hand-edited.
- Developer self-tests (`--meeting-selftest`, `--refine-selftest`) are the substitute for a test target: reproducible, log-only, exit on their own. Extend them rather than adding ad-hoc debug UI.
