# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ReplayCapture is a ShadowPlay-style instant-replay tool for Windows: it keeps the last 45–60 seconds
of every display in memory continuously and writes it to disk on Alt+F10, one `.mov` per display,
each carrying an arbitrary number of separate audio stems. C# on `net10.0-windows10.0.26100.0`,
x64-only (NVENC, D3D11, and the FFmpeg native runtime are all x64-only). See `README.md` for the
full design rationale, audio-source grammar, verified hardware behavior, and a "four bugs worth
remembering" section — read it before touching capture, encoding, muxing, or audio-routing code.

## Build and test

```bash
dotnet build S:\_replayCapture\ReplayCapture.slnx -c Release
dotnet test S:\_replayCapture\ReplayCapture.slnx
```

Run a single test class or method with the standard xUnit filter:

```bash
dotnet test S:\_replayCapture\ReplayCapture.slnx --filter "FullyQualifiedName~PacketRingBufferTests"
```

The app is tray-only (no main window). After any XAML/binding change, run the UI self-test — WPF
defers binding/template errors to runtime, so a bad `MultiBinding` or template compiles clean and
only fails when a control is first realized:

```bash
ReplayCapture.exe --selftest
```

`tools/ReplayCapture.Probe` (built as `rcprobe.exe`) is the developer harness for exercising pipeline
stages without launching the elevated tray app: `rcprobe displays`, `rcprobe capture <n>`,
`rcprobe record <seconds> <outDir>`, `rcprobe audio`, `rcprobe sessions` (shows which track each
live audio session would be assigned to — the fast way to check a config change), `rcprobe bench
<seconds>`, `rcprobe rebuild` (exercises the resolution-change recovery path).

`Directory.Build.props` fixes `InvariantGlobalization=false` for every project — never turn this on;
see the comment there and the README's bug writeup for why it takes down WPF's binding engine.

## Architecture

**`ReplayCapture.Core`** (the pipeline, UI-agnostic) → **`ReplayCapture.App`** (WPF tray shell) →
**`ReplayCapture.Probe`** (CLI harness, same Core, no UI) → **`ReplayCapture.Tests`** (xUnit against
Core; internals are exposed via `InternalsVisibleTo` since the process/track rules engine is
internal but is exactly the logic worth testing).

### Core pipeline, top to bottom

- `ReplaySession` is the whole always-on buffer: one `D3DContext` (shared GPU device) plus one
  `DisplayRecorder` per attached display plus one `AudioEngine`, all pinned to a single shared clock
  origin (`EpochQpc`). It owns display selection (`SelectDisplays` — empty config means "capture
  everything attached"), the 3-second watchdog that detects a lost GPU or a changed display set and
  raises `RecoveryRequired`, and `Save()`, which snapshots every recorder's ring buffer and the audio
  engine and muxes them into aligned `.mov` files sharing one origin timestamp.
- `DisplayRecorder` is one display's full pipeline: `DisplayCaptureSource` (Windows.Graphics.Capture)
  → `FramePacer` (paces ticks to the configured fps, inventing duplicate frames rather than stalling)
  → `NvencVideoEncoder` → `PacketRingBuffer`. A resolution change sets `_rebuildRequested`; the pacer
  rebuilds capture + encoder on its next tick and **discards** the ring buffer, because H.264
  parameter sets are dimension-specific and mixing pre/post-resize packets would produce an
  unreadable file.
- `AudioEngine` (`Audio/`) owns every configured `AudioTrackConfig`, each backed by one or more
  sources resolved from `AudioSourceSpec` (device loopback, mic capture, per-process loopback, or a
  named group from `AppConfig.ProcessGroups`). `AudioSessionMonitor` enumerates only processes that
  currently hold an audio session (there's no way to attach process-loopback to "everything except
  X" directly, and enumerating all running processes doesn't scale) and `ProcessTrackBinding`
  resolves which track each session belongs to, expanding `group:` the same way
  `AudioSourceSpec.TryResolveGroup` does so live matching and `rcprobe sessions`' static config check
  never disagree.
- `Muxing/MovWriter` writes QuickTime `.mov` (chosen over MP4/MKV — see README) with one video
  stream and N audio streams; track names surface as QuickTime `handler_name` boxes.
- `Timing/Clock` + `FramePacer` are the shared timebase: everything is timestamped in QPC ticks
  against `ReplaySession.EpochQpc`, which is what lets independently-started per-display video and
  per-track audio all line up on one timeline at save time with no manual offset.
- `Config/ConfigStore` reads/writes `%APPDATA%\ReplayCapture\config.json`; `AppConfig` documents
  every field's default and why (e.g. `MaxRingMemoryMegabytes` is divided by displays *actually
  captured*, not displays *named in config*, per the README bug writeup).

### App shell

`App.xaml.cs` is the composition root: builds `ReplaySession` off the UI thread (`StartSession`),
wires `TrayController` (menu, notifications, armed/saving state), `GlobalHotkeyService` (elevated
`RegisterHotKey`, since UIPI drops hotkeys and low-level keyboard hooks while a higher-integrity
window has focus — this is why the app requires admin, see README), `IndicatorWindow` (the
armed-state overlay, excluded from its own capture), and `StartupTaskInstaller` (a Task Scheduler
logon entry at `RunLevel=HighestAvailable`, not a Run key, because only Task Scheduler can launch
elevated without a UAC prompt). `SelfTest.Run()` (`--selftest`) opens every window and the tray
context menu and exits non-zero on failure. Config changes only restart the pipeline
(`RestartSession`) when they change its shape (buffer size, memory cap, display list, audio track
list); everything else (hotkey, indicator, startup toggle) applies live so the buffer isn't emptied
needlessly.

Recovery is triggered from two places: `ReplaySession.RecoveryRequired` (GPU loss, capture-surface
closed, display topology changed) and `SystemEvents.PowerModeChanged` on resume-from-sleep (WASAPI
endpoints can be silently invalidated by sleep with no error, so resume always rebuilds
unconditionally after a 3s settle delay rather than trying to diagnose which part broke). A rebuild
is deferred rather than forced while a save is in flight, since the save is reading ring buffers the
rebuild would dispose out from under it.
