# ReplayCapture

A ShadowPlay-style instant replay buffer for Windows. Keeps the last 45–60 seconds of every display
in memory at all times and writes it to disk on **Alt+F10**, with each screen as its own video file
and an arbitrary number of separate audio stems.

## Design decisions

| | |
|---|---|
| Stack | C# on `net10.0-windows10.0.26100.0`, x64 only |
| Capture | Windows.Graphics.Capture, one session per display |
| Colour convert | `ID3D11VideoProcessor` (fixed-function BGRA→NV12, BT.709 limited range) |
| Encode | NVENC `h264_nvenc` via Sdcb.FFmpeg, sharing the capture `ID3D11Device` |
| Container | **`.mov`**, one file per display, each carrying every audio track |
| Audio | Uncompressed PCM s16le, one track per stem |

The container choice is load-bearing: **Premiere Pro cannot import MKV at all**, and its multi-audio
-track support in MP4 is unreliable (it frequently exposes only the first track). QuickTime `.mov`
with multiple tracks is the production standard and imports with no conforming and no re-encode.

## Elevation is a requirement, not a preference

UIPI drops both `RegisterHotKey` delivery and `WH_KEYBOARD_LL` events when a **higher-integrity**
window holds focus. A medium-integrity process therefore cannot see Alt+F10 while Task Manager, an
admin console, or an anti-cheat-protected game is in front. There is no user-mode way around this,
so `app.manifest` requests `requireAdministrator`.

To avoid a UAC prompt on every boot, `StartupTaskInstaller` registers a Task Scheduler entry
(logon trigger, `RunLevel=HighestAvailable`, 20 s delay) rather than a Run-key or Startup-folder
shortcut, neither of which can launch elevated silently.

Elevation is also what lets Windows.Graphics.Capture see elevated windows.

## Build and test

```bash
dotnet build S:\_replayCapture\ReplayCapture.slnx -c Release
```

```bash
dotnet test S:\_replayCapture\ReplayCapture.slnx
```

The app is tray-only — it has no main window. Right-click the notification-area icon for the menu.

## Configuration

`%APPDATA%\ReplayCapture\config.json`, written with defaults on first run. Logs land in
`%APPDATA%\ReplayCapture\logs` (last 10 runs retained).

Audio tracks are a plain array — **six is the default layout, not a limit**. Add a seventh and the
muxer writes a seventh track.

### Audio source grammar

| Spec | Meaning |
|---|---|
| `device:render:default` | Current default playback device, captured as loopback |
| `device:capture:default` | Current default microphone |
| `device:render:{endpointId}` | A specific endpoint, pinned by MMDevice id |
| `proc:spotify.exe` | Include this process and its child tree |
| `proc:!discord.exe` | Exclude this process |
| `proc:*` | Include everything; combine with exclusions for an "everything else" stem |

Executable patterns accept `*` and `?` and match case-insensitively.

Any process named on one track must be excluded from `proc:*` tracks, or its audio lands on two
stems at once. `AppConfigTests.Game_track_captures_everything_not_claimed_elsewhere` enforces this
for the shipped defaults.

## Status

| Milestone | State |
|---|---|
| **M0** — scaffold, config, tray, hotkey, elevation, startup task | code complete; awaiting the elevated-hotkey acceptance test |
| **M1** — single-display capture → `.mov` | **done**, verified end to end |
| **M2** — CFR pacing, IDR trim, BT.709 tagging | **done**; ffprobe-verified, Premiere import still to confirm |
| **M3** — mic + desktop loopback audio | **done**, verified sample-accurate |
| **M4** — per-process loopback and track rules | **done**, verified isolating a real process |
| M5 — multi-display | session-level plumbing done; needs a second display to verify |
| **M6** — overlay indicator and settings UI | **done**; tray app is wired to the pipeline |
| **M7** — robustness and overhead | **done**; measured below |

Alt+F10 (or the tray menu) writes the buffered window to disk. Settings live in the tray menu; the
armed indicator sits in a configurable corner and is excluded from capture.

### UI self-test

WPF defers almost everything to runtime — a XAML typo or bad binding compiles cleanly and only
fails when the control is first realised. Run this after any UI change:

```bash
ReplayCapture.exe --selftest
```

It exercises every window and opens the tray context menu, then exits non-zero on any failure.
Realising that menu's templates is what caught the globalization crash described below.

### Developer harness

`tools/ReplayCapture.Probe` builds as `rcprobe.exe`:

```bash
rcprobe displays
```

```bash
rcprobe record 10 S:\_replayCapture\out
```

```bash
rcprobe audio
```

```bash
rcprobe sessions
```

`sessions` shows every process currently holding an audio session and which track the rules assign
it to — the fastest way to check a config change did what you meant, without starting a capture.

`rcprobe capture 3` exercises WGC and the NV12 converter without touching the encoder, which is
where to start if frames stop arriving.

### Verified on this machine

- FFmpeg runtime `n7.1-58-g10aaf84f85` ships `h264_nvenc`, `hevc_nvenc`, `av1_nvenc`, `pcm_s16le`,
  the `d3d11va` hwdevice and the `mov` muxer.
- An `h264_nvenc` encode session opens successfully on the RTX 5070.
- CsWin32 has full metadata for the per-process loopback surface
  (`ActivateAudioInterfaceAsync`, `AUDIOCLIENT_ACTIVATION_PARAMS`,
  `IActivateAudioInterfaceCompletionHandler`, `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK`).
- A 10-second capture of a 1920×1080@60 display produced `h264` High / `avc1` in `mov`,
  `bt709` primaries+transfer+matrix with `color_range=tv`, `r_frame_rate == avg_frame_rate == 60/1`,
  604 frames with **every PTS delta identical** (true CFR), 1-second GOP, 40.35 Mbps, and a clip
  duration of 10.07 s against a 10 s request.

- A 10-second capture with a synthesised 440 Hz tone at amplitude 12000 playing produced six PCM
  tracks: **Desktop peaked at 11999** (sample-accurate through WASAPI loopback, resampler, timeline
  buffer, PCM16 and the muxer), Mic carried real room noise, **Desktop + Mic peaked at 12322 ≈ the
  sum of both**, and Game/Communications/Music were byte-identical digital silence.
- Track names reach the file as QuickTime `handler_name`, which is the box NLEs display. MOV has no
  per-track `title`.
- With a 12000-amplitude tone playing from `powershell.exe` and Discord running idle:
  **Game peaked at exactly 12000** (per-process loopback taps *before* the endpoint mix, so it sees
  the source amplitude, while Desktop saw 11996 post-mix), and **Communications was pure silence**
  despite being attached to Discord. That is the per-process isolation the six-stem layout exists for.

### `proc:*` is evaluated against audio sessions, not against all processes

"Everything except Spotify and Discord" cannot be expressed with process loopback's exclude mode,
which takes a single target PID, and attaching an include-mode client to every running process would
mean hundreds of clients. Only processes holding an audio session can make sound — typically fewer
than ten — so `AudioSessionMonitor` enumerates those and the rules are evaluated against that set.

### Measured overhead

`rcprobe bench 30`, one 1920×1080@60 display at 40 Mbps with six audio tracks and a 30 s buffer:

| | |
|---|---|
| GPU utilisation | 3.0% idle → 4.8% armed = **+1.8%** |
| Process CPU | **0.40%** of all cores (Ryzen 7 7800X3D) |
| Working set / ring buffers | 369 MB / 214 MB |
| **Frame-rate accuracy** | **100.04%** (1810 frames against 1809 expected) |
| Late pacer ticks | **0** |
| NVENC latency | ~498 µs |

That is comfortably under the 2–4% GPU budget the plan assumed. Note `encoder.stats.sessionCount`
reads 2 on this machine because Sunshine holds an NVENC session of its own.

### Recovery

`rcprobe rebuild` forces the path a resolution change takes — recreate the frame pool, rebuild the
encoder, discard the ring — and verifies the pipeline resumes. It does so without touching display
settings. Last run: rebuilt in under 1.5 s, resumed at 60 fps with zero late ticks, and the
post-rebuild clip decoded clean at 570 frames of exact 60/1 CFR with all six audio tracks.

The buffer is deliberately **discarded** on rebuild. A single `.mov` track cannot change resolution
partway through and H.264 parameter sets are dimension-specific, so keeping the old packets would
produce a file no decoder can read. Losing history beats writing a corrupt clip.

A watchdog in `ReplaySession` checks every 3 s for a lost GPU (driver update, TDR) and for displays
appearing or disappearing, and raises `RecoveryRequired` — the app rebuilds the session in response,
deferring if a save is in flight.

### Four bugs worth remembering

**Divide a shared budget by what you actually built, not by what config asked for.** The ring memory
cap was split across `config.Displays.Count(d => d.Enabled)`. A default config names no displays at
all (empty means "capture everything attached"), so that count was zero, the `Math.Max(1, …)` guard
turned it into one, and *every* display received the full cap — two screens quietly used twice the
configured ceiling. It now divides by the number of recorders actually created.

**Never set `InvariantGlobalization=true` in a WPF app.** It compiles and starts fine, then throws
`XamlParseException: Cannot find non-neutral culture related to 'en-us'` the first time a templated
control containing a MultiBinding is realised — which took down the tray context menu on the very
first right-click. WPF's binding engine calls `XmlLanguage.GetSpecificCulture()` and needs real ICU
data. Caught for good by `--selftest`.

**CsWinRT objects are not COM RCWs.** Casting an `IDirect3DSurface` to a `[ComImport]`
`IDirect3DDxgiInterfaceAccess` throws `InvalidCastException`; the interface has to be reached with
an explicit `QueryInterface` on the native pointer and a vtable call. See `Direct3DInterop`.

**Round, don't floor, when converting ticks to sample frames.** One frame at 48 kHz is 208.33 ticks,
so flooring places every audio block up to 20.8 µs late and breaks the frame↔tick round trip
(frame 1 → 208 ticks → frame 0). See `AudioFormat.TicksToFrames`.

**Trim one GOP later than feels natural.** Dropping the leading GOP the moment its own keyframe
ages out leaves the buffer starting at a keyframe *newer* than the cutoff, so saves come out short
— a 10 s request produced 9.07 s. The buffer must keep the leading GOP until the *following* one
covers the window. Locked down by `PacketRingBufferTests`.
