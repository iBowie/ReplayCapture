# Linux support — status and how to resume

Working notes for continuing the Linux port. The full phased plan (context, architecture,
all 7 phases, verification approach) lives at:

    C:\Users\admin\.claude\plans\plan-linux-support-rippling-ladybug.md

Read that first for the *why* and the full roadmap. This file is the *where-we-left-off* —
what's actually landed, what's still a guess, and what to do next.

## Scope decisions (already made, don't relitigate)

- Target: **full feature parity** with the Windows app (tray, hotkey, startup, indicator
  overlay, per-process audio) — not just a headless capture/encode pipeline.
- Display capture: **PipeWire via xdg-desktop-portal ScreenCast** first; X11 is an explicit
  later phase.
- GPU encode: **VAAPI** first; NVENC/CUDA on Linux is out of scope.
- Audio: **per-process capture is in scope from the start**, via PipeWire per-node stream
  linking (no WASAPI-equivalent API exists — this is new design, not a port).

## Status by phase

| Phase | Status | Notes |
|---|---|---|
| 1 — Generic pipeline refactor | **Done, committed** (`5c52215 decouple windows pipeline`) | `IDisplayCaptureSource<TFrame>`, `IVideoEncoder<TFrame>`, `DisplayRecorder<TFrame>` + `IDisplayRecorder` marker interface. Windows-only, zero behavior change, 117 tests green. |
| TFM/build plumbing | **Done, uncommitted** | Core multi-targets `net10.0-windows10.0.26100.0;net10.0`. All genuinely Windows-only files moved into `Windows/` sibling folders; found and fixed a real blocker (WinRT `SizeInt32` leaking into the "portable" interfaces — replaced with a new portable `FrameSize` struct). `FramePacer`'s high-res timer split behind `#if WINDOWS`. Both TFMs build clean, tests green. |
| 2 — VAAPI encode spike | **Not started** | No `VaapiContext`/`VaapiVideoEncoder`/`VaapiVppConverter` written yet. |
| 3 — PipeWire capture spike | **Drafted, uncommitted, unverified** | See below — compiles on both TFMs but has never touched real PipeWire/a real compositor. |
| 4–7 | **Not started** | |

**Nothing in this session has been committed except Phase 1.** Everything else (TFM
plumbing + Phase 3 draft) is sitting uncommitted in the working tree — review with
`git status`/`git diff` before committing.

## What's actually in the repo right now

### Build/TFM plumbing (`Directory.Build.props`, `ReplayCapture.Core.csproj`)

- Only `ReplayCapture.Core` multi-targets; `App`/`Probe`/`Tests` stay Windows-only for now.
- Folder convention: `Capture/Windows/`, `Encoders/Windows/`, `Audio/Windows/`,
  `Windows/ReplaySession.cs` hold everything genuinely Windows-only, excluded from the
  Linux TFM via `<Compile Remove>` in the csproj. `Capture/Linux/` (new) is excluded from
  the Windows TFM the same way, in reverse.
- **`ReplaySession`/`AudioEngine`/`ProcessTrackBinding` are Windows-only for now** —
  deliberately not abstracted behind a fake seam, since they're saturated with
  WASAPI-specific logic throughout their bodies and abstracting that meaningfully is
  Phase 5's real work. Core's Linux leg currently only exposes what's genuinely portable
  already: `MovWriter`, `Clock`, `FramePacer`, `AudioTrackBuffer`/`AudioResampler`/
  `AudioFormat`, the config schema (`AudioSourceSpec`/`AppConfig`), and the generic
  `DisplayRecorder<TFrame>`/`IVideoEncoder<TFrame>`/`IDisplayCaptureSource<TFrame>`
  abstractions.
- `FrameSize` (new, `Capture/FrameSize.cs`) replaces `Windows.Graphics.SizeInt32` in the
  shared interfaces — the WinRT type doesn't exist outside a Windows-versioned TFM and
  would have silently broken Linux compilation later. The two Windows capture sources
  convert to/from WinRT's type at their own boundary.
- Confirmed via NuGet: **`Sdcb.FFmpeg.runtime.linux-x64` does not exist.** The Linux leg
  references only `Sdcb.FFmpeg` (no runtime asset) and will need to resolve native FFmpeg
  libraries at runtime via `DynamicallyLoadedBindings.LibrariesPath` pointed at the
  distro's system FFmpeg, once a Linux backend actually calls into it (Phase 2 concern).

### Phase 3 draft (`Capture/Linux/`)

Files: `VaapiFrame.cs`, `PipeWireBuffer.cs`, `DmaBufImporter.cs` (stub —
`NotImplementedException`, needs Phase 2's `VaapiContext`), `PipeWireStream.cs`,
`Interop/PipeWireInterop.cs`, `Interop/SpaDataType.cs`, `Interop/SpaPodBuilder.cs`,
`Portal/ScreenCastPortalSession.cs`.

**Design note:** `PipeWireStream` implements `IDisplayCaptureSource<PipeWireBuffer>`, not
`IDisplayCaptureSource<VaapiFrame>` — it can only produce what PipeWire hands back
(a buffer that may or may not be DMA-BUF-backed). Turning that into a `VaapiFrame` needs a
small adapter wrapping this class once Phase 2's `VaapiContext` exists. Don't collapse
these into one class when resuming — the split is deliberate and matches Phase 1's generic
`TFrame` seam.

**What's verified vs. guessed, precisely:**

- `Portal/ScreenCastPortalSession.cs` (the D-Bus xdg-desktop-portal negotiation) compiles
  against the **real, decompiled** `Tmds.DBus.Protocol` 0.94.2 API — not guessed from
  memory. (Method: added the package, wrote a throwaway metadata-reader console app against
  the cached NuGet DLL to dump real class/method signatures, then iterated against actual
  compiler errors. See the technique below if this needs re-verifying against a different
  package version.) The CreateSession → SelectSources → Start → OpenPipeWireRemote sequence
  and the Request/Response-signal wait pattern reflect the documented, stable portal
  protocol spec. What's **not** verified: whether the predicted request object path always
  matches what a real compositor returns (spec allows it not to — unhandled, throws
  `NotSupportedException` if it happens), the exact runtime shape of the `streams` result,
  and any GNOME-vs-KDE behavioral differences.
- `Interop/PipeWireInterop.cs`'s native struct layouts (`pw_stream_events`, `spa_buffer`,
  `spa_data`, `spa_chunk`, `pw_stream_state`) are hand-authored from memory of PipeWire's C
  headers with **no way to check them in this environment**. This is the most dangerous
  unverified part of the whole draft — a layout mismatch corrupts memory or invokes the
  wrong callback silently rather than throwing. **Before trusting this with anything beyond
  logging from a single callback**, check every struct against
  `pkg-config --cflags libpipewire-0.3`'s actual header on the real target distro/PipeWire
  version, and validate incrementally (e.g. confirm `state_changed` fires correctly on its
  own before trusting `process`).
- `Interop/SpaPodBuilder.cs` (SPA POD binary serialization) is the **least trustworthy file
  in the set** — it hand-expands a binary format normally built via C macros, with no
  header to check against, and only offers one fixed video format/size instead of a proper
  `Choice` enumeration a real compositor likely expects. Expect to rewrite this once it can
  be tested against a real `spa_debug_pod` dump.

## How to re-verify a native/managed API you're unsure about (reusable technique)

For a **managed** NuGet package (like `Tmds.DBus.Protocol`), don't guess from memory or
stale docs — decompile the actually-restored DLL's metadata directly:

1. `dotnet restore` the project so the package lands in `~/.nuget/packages/<name>/<version>/`.
2. Write a tiny throwaway console app (net10.0) using `System.Reflection.Metadata.PEReader`
   + `MetadataReader` to walk `TypeDefinitions`/`GetMethods()`/`DecodeSignature` and print
   public type/method signatures straight from the DLL — this avoids the dependency-load
   failures `Assembly.LoadFrom` hits on unrelated/incompatible hosts (e.g. Windows
   PowerShell 5.1 can't load a modern net8/net10 assembly's dependency graph at all).
3. Filter by type-name substring once you know roughly what you're looking for.
4. Write code against the real signatures, then `dotnet build -f <tfm>` and iterate on
   actual compiler errors — this is strictly better than guessing, since wrong method
   names/argument types fail to compile immediately instead of failing silently at runtime.

For a **native** C library (like `libpipewire`), there is no equivalent shortcut without
the actual headers or a real instance of the library to link/run against — P/Invoke
declarations compile regardless of whether they're correct (they're just metadata), so a
successful Linux-TFM build proves nothing about `PipeWireInterop.cs`'s correctness. That
part can only be verified on real Linux hardware.

## Immediate next steps (pick up here)

1. **Review and decide what to commit.** `git status`/`git diff` — Phase 1 is already
   committed; everything else (TFM plumbing, `FrameSize`, `FramePacer` split, Phase 3
   draft) is uncommitted.
2. **Phase 2 (VAAPI encode)** is unstarted and is the natural next coding step if you still
   don't have Linux hardware — like Phase 3, it can be scaffolded against `Sdcb.FFmpeg.Raw`
   in a similar "iterate against real compiler errors" way, but the actual `vaInitialize`/
   driver behavior needs real hardware regardless.
3. **The moment you have a real Linux box with a Wayland compositor + PipeWire +
   xdg-desktop-portal running:** the highest-value first test is `rcprobe portal-probe`
   (not written yet — add a minimal verb that just runs `ScreenCastPortalSession.StartAsync()`
   and dumps the negotiated node id / fd / any exceptions to console). That single test
   will immediately tell you which of the "unverified" items above actually needed fixing.
4. Don't build Phase 4+ (wiring capture→encode→mux) until Phase 2 and Phase 3 have each
   independently proven themselves against real hardware — per the plan's own sequencing,
   an early full-pipeline wire-up is not the right first test.

## Build commands

```bash
# Windows leg (regression gate — must stay green through every phase)
dotnet build S:\_replayCapture\ReplayCapture.slnx -c Release
dotnet test S:\_replayCapture\ReplayCapture.slnx -c Release

# Linux leg (Core only, compiles on Windows, proves nothing about runtime correctness)
dotnet build src/ReplayCapture.Core/ReplayCapture.Core.csproj -c Release -f net10.0
```
