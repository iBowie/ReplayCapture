# Changelog

All notable changes to ReplayCapture, tracked from `v0.1.0-alpha` onward.

## [Unreleased]

### Changed
- **Notifications are an on-screen overlay instead of tray balloons.** A shell balloon is an
  ordinary desktop window, so "Replay saved — 60s" was composited into the desktop and recorded into
  the *next* clip saved within the buffer window. The new `NotificationWindow` carries
  `WDA_EXCLUDEFROMCAPTURE` like the armed indicator does, is click-through and never activates, and
  sits just under (or above) the indicator in the same corner. Every notification the app raises now
  goes through it. Settings → "Show notifications as an on-screen overlay instead of tray balloons"
  (`UseOverlayNotifications`, default on) switches back to balloons, which persist in the Action
  Center after they fade.
- **The save chime is no longer audible in later replays.** Two things were wrong. The chime was
  `SystemSounds.Asterisk`, i.e. `MessageBeep`, which Windows renders through its own shared "System
  Sounds" session rather than through this process — nothing this app owns could have excluded it.
  It is now a short synthesised two-tone cue played with `SoundPlayer`, which opens a render stream
  inside this process. And `device:render:default` is now captured as a process loopback that
  excludes this app's own process tree (`AppConfig.ExcludeOwnAudioFromLoopback`, default on) rather
  than as an endpoint loopback of the final mix, which by definition already contained anything the
  app had played and offered no way to opt out. Per-process stems already skipped our own pid, so
  only the desktop stems were affected. Trade-offs, both usually invisible: audio played to a
  *different* output device now also lands on those tracks (process loopback follows processes, not
  endpoints), and Windows' own system sounds may not be captured at all. Turning the option off
  restores endpoint loopback; so does an activation failure, which is logged rather than silent.
  Pinned `device:render:{id}` sources are unaffected — process loopback cannot be aimed at a
  specific endpoint.

## [v0.2.0-alpha] — 2026-08-22

### Added
- **Per-display resolution override.** `DisplayConfig.CaptureWidth`/`CaptureHeight` (Settings →
  Displays → Width/Height) pin a display's encode size independently of its native resolution; the
  Nv12 colour-conversion pass scales into it for free. Leave both blank to keep encoding at native
  size.
- **Configurable blank-display timeout.** Settings → "Ditch black display after (s)" frees a
  display's capture/encoder resources once it has shown nothing but black for that long (0 disables
  it), instead of holding those resources for a display that may never come back.
- **Audio track routing preview.** Settings → Audio tracks now lists currently-running audio
  processes and shows which track each one would be routed to under the sources as currently
  edited — including unsaved edits and processes that aren't running yet — via the new
  `ProcessTrackRouter`.
- **Disconnected displays stay visible in Settings.** A display present in config but not currently
  attached (unplugged, docked laptop closed, etc.) is still listed, marked "not connected", instead
  of silently dropping its saved settings.
- Displays are now identified by a stable monitor ID rather than the OS device name, so a config
  entry survives GPU/output re-enumeration across driver updates and re-plugs.
- `rcprobe rebuild` and a new `DisplayRecorderResilienceTests` suite exercise the resize-recovery and
  UAC/secure-desktop access-loss paths directly.
- **Current version shown in the app.** Build version (derived from `git describe`, e.g.
  `0.2.0-alpha+0.<hash>`) appears as a grayed-out row in the tray context menu and in the Settings
  window's title bar. No new dependency — `Directory.Build.props`' `SetVersionFromGit` target parses
  `git describe` at build time, the same "hand-roll it" call already made for the tray icon and
  global hotkeys.
- This changelog.

### Changed
- **Resolution changes no longer discard the replay buffer.** Previously a display resolution change
  tore down the encoder and discarded that display's whole ring buffer, because H.264 parameter sets
  are dimension-specific. Each display's encode resolution is now fixed for the life of its
  recorder; a resolution change re-provisions capture at the new native size and tells the encoder
  (`IVideoEncoder.NotifySourceResized`) to scale into the unchanged encode size instead. Trade-off:
  after a resolution change, the saved clip is a GPU-scaled copy of the new native frames at the
  *original* encode resolution, not a native capture of the display's current mode.
- **A display with nothing to offer now encodes a black frame instead of skipping the tick.** No
  frame yet, or the display is temporarily unavailable (disconnected, asleep, mid-reacquire after
  DXGI access loss) — the pacer encodes `IDisplayCaptureSource.BlackFrame` rather than leaving a
  silent gap in the saved clip. `DisplayRecorder.BlankFrames` counts how often this happens.

### Fixed
- **A UAC prompt or secure-desktop transition on the elevated capture process no longer permanently
  freezes DXGI Desktop Duplication capture for that display.** The prior recovery path only treated
  the documented `DXGI_ERROR_ACCESS_LOST` as recoverable and looped forever calling
  `AcquireNextFrame` on the same dead interface for anything else — and a secure-desktop transition
  has been observed (RTX 50-series, driver-dependent) to surface as `DXGI_ERROR_INVALID_CALL`
  instead, which hit exactly that loop. Any post-`WaitTimeout` failure now tears the duplication
  interface down and re-acquires; recovery gives up (tearing the recorder down) only after 60
  continuous seconds of failure, long enough to survive a UAC prompt or lock screen without a user
  losing their whole buffer over it.

### Internal
- Split `ReplayCapture.Core` into a Windows-specific tree (`Capture/Windows`, `Encoders/Windows`,
  `Audio/Windows`, `Windows/ReplaySession.cs`) plus platform-portable pieces, and added a stubbed
  Linux capture backend (PipeWire/xdg-desktop-portal ScreenCast, VA-API) behind the same
  `IDisplayCaptureSource` abstraction — not wired up or usable yet, but `ReplayCapture.Core` now
  builds and tests against a `net10.0` (non-Windows) TFM in addition to
  `net10.0-windows10.0.26100.0`. See `LINUX_SUPPORT.md`.

## [v0.1.0-alpha] — first tagged build

Initial always-on instant-replay pipeline: DXGI Desktop Duplication / Windows.Graphics.Capture →
NVENC/AMF/x264 → per-display ring buffer, WASAPI-based multi-track audio (device loopback, mic,
per-process loopback, named process groups), QuickTime `.mov` muxing with N audio streams, Avalonia
tray shell with global hotkey save, armed-state indicator overlay, and Task Scheduler-based elevated
startup.
