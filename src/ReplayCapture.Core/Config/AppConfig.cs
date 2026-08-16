using System.Text.Json.Serialization;

namespace ReplayCapture.Core.Config;

public sealed record AppConfig
{
    /// <summary>How much history the ring buffer guarantees. Clips come out between this and this + 1s.</summary>
    public int BufferSeconds { get; init; } = 60;

    public string OutputDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Replays");

    public string Hotkey { get; init; } = "Alt+F10";

    /// <summary>Register a scheduled task so the (elevated) app starts at logon without a UAC prompt.</summary>
    public bool StartWithWindows { get; init; } = true;

    public bool PlaySoundOnSave { get; init; } = true;
    public bool ShowOverlayIndicator { get; init; } = true;

    /// <summary>Which corner of the primary display the armed indicator sits in.</summary>
    public OverlayCorner OverlayCorner { get; init; } = OverlayCorner.TopRight;

    /// <summary>
    /// Hard ceiling on ring-buffer memory across all displays and audio tracks. When the projected
    /// footprint exceeds this, buffer seconds are reduced rather than letting the process balloon.
    /// </summary>
    public int MaxRingMemoryMegabytes { get; init; } = 2048;

    /// <summary>Which native API captures each display's frames. See <see cref="CaptureBackend"/>.</summary>
    public CaptureBackend CaptureBackend { get; init; } = CaptureBackend.Dxgi;

    /// <summary>Which engine encodes every display's H.264 stream. See <see cref="VideoEncoderBackend"/>.</summary>
    public VideoEncoderBackend VideoEncoderBackend { get; init; } = VideoEncoderBackend.Nvenc;

    public IReadOnlyList<DisplayConfig> Displays { get; init; } = [];

    public IReadOnlyList<AudioTrackConfig> AudioTracks { get; init; } = DefaultAudioTracks;

    /// <summary>
    /// Named sets of executable patterns, so a track rule can say <c>group:comms</c> instead of
    /// spelling out every app in the group with <c>proc:</c>. Looked up case-insensitively; entries
    /// support the same <c>*</c>/<c>?</c> wildcards as <c>proc:</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProcessGroups { get; init; } = DefaultProcessGroups;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultProcessGroups => new Dictionary<string, IReadOnlyList<string>>
    {
        ["comms"] = ["discord.exe", "ms-teams.exe", "slack.exe", "telegram.exe"],
        ["music"] = ["spotify.exe", "foobar2000.exe"],
    };

    /// <summary>
    /// Six tracks is the default layout, not a limit — the muxer writes however many are configured.
    /// </summary>
    public static IReadOnlyList<AudioTrackConfig> DefaultAudioTracks =>
    [
        new()
        {
            Name = "Desktop + Mic",
            Sources = ["device:render:default", "device:capture:default"],
        },
        new() { Name = "Desktop", Sources = ["device:render:default"] },
        new() { Name = "Mic", Sources = ["device:capture:default"] },
        new()
        {
            // "Everything that isn't claimed by another track" — the usual definition of game audio.
            Name = "Game",
            // Every process named on another track must be excluded here, or its audio would be
            // duplicated across two stems. AppConfigTests enforces that invariant.
            Sources =
            [
                "proc:*",
                "proc:!spotify.exe",
                "proc:!discord.exe",
                "proc:!ms-teams.exe",
                "proc:!slack.exe",
                "proc:!foobar2000.exe",
            ],
        },
        new()
        {
            Name = "Communications",
            Sources = ["proc:discord.exe", "proc:ms-teams.exe", "proc:slack.exe"],
        },
        new()
        {
            Name = "Music",
            Sources = ["proc:spotify.exe", "proc:foobar2000.exe"],
        },
    ];
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Which native API <see cref="Capture.DisplayCaptureSourceFactory"/> uses to pull frames off each
/// display. Applies to every display; a resolution/refresh mismatch between two monitors doesn't
/// change which backend suits either of them, so there is one setting rather than one per display.
/// <para>
/// <b><see cref="Dxgi"/></b> (the default) — raw <c>IDXGIOutputDuplication</c> ("Desktop
/// Duplication"). On this hardware it delivers a 200Hz display's real refresh rate (~207
/// frames/sec measured); WGC on the same monitor capped out around ~50/sec regardless of
/// frame-pool buffer count or GPU load, a limit that lives inside WGC's own compositor layer, not
/// anything this app controls. Desktop Duplication hands back the cursor as separate shape/position
/// metadata rather than compositing it in, so <c>DxgiDisplayCaptureSource</c> draws it back on with
/// a GPU alpha-blend (<c>CursorOverlay</c>) — the trade-off is one rare edge case: the legacy
/// monochrome/masked-color "invert" cursor pixel renders transparent instead of inverted, since a
/// pure blend never reads the destination. It also runs a dedicated polling thread per display that
/// never fully idles, even when nothing on screen is changing.
/// </para>
/// <para>
/// <b><see cref="Wgc"/></b> — Windows.Graphics.Capture. Event-driven: a frame-arrived callback that
/// costs nothing while the screen is static, versus Desktop Duplication's always-spinning polling
/// thread — the one real reason to prefer it. It composites the cursor (including that legacy
/// invert pixel) correctly with no extra work. The cost is the frame-rate cap above, so it is the
/// wrong choice for capturing a high-refresh-rate display (120Hz+) at its native rate; it is a
/// reasonable choice on a 60Hz-or-lower display, mostly-idle content (a second monitor showing a
/// dashboard, chat, or a stream overlay), or a machine where the always-on polling thread's CPU cost
/// actually matters more than frame-rate accuracy.
/// </para>
/// </summary>
public enum CaptureBackend
{
    Dxgi,
    Wgc,
}

/// <summary>
/// Which encode engine <see cref="Encoders.VideoEncoderFactory"/> uses for every display. Applies
/// globally, not per display — the available encode engines are a whole-machine GPU-vendor fact,
/// not something that varies sensibly between two monitors on the same box.
/// <para>
/// <b><see cref="Nvenc"/></b> (the default) — NVIDIA NVENC via FFmpeg's <c>h264_nvenc</c>. Fully
/// GPU-resident: the captured texture is colour-converted straight into an NVENC input surface with
/// no CPU round-trip. Requires an NVIDIA GPU with a hardware encoder.
/// </para>
/// <para>
/// <b><see cref="Amf"/></b> — AMD's Advanced Media Framework via FFmpeg's <c>h264_amf</c>. The same
/// fully GPU-resident path as NVENC: AMF also consumes a D3D11 hardware frames context directly, so
/// the frame never leaves the GPU. Requires an AMD GPU with a hardware H.264 encoder (VCE).
/// </para>
/// <para>
/// <b><see cref="X264"/></b> — software encoding via FFmpeg's <c>libx264</c>, for machines with
/// neither an NVENC nor an AMF encoder available, or where the hardware encoder is already committed
/// elsewhere (e.g. a separate streaming pipeline). Unlike the two hardware paths, every captured
/// frame has to be read back from the GPU into system memory before x264 can see it — a PCIe
/// round-trip and a CPU-side encode that the hardware paths never pay — so expect materially higher
/// CPU usage and, on a loaded system, a lower sustainable frame rate.
/// </para>
/// </summary>
public enum VideoEncoderBackend
{
    Nvenc,
    Amf,
    X264,
}

public sealed record DisplayConfig
{
    /// <summary>GDI device name, e.g. <c>\\.\DISPLAY1</c>. Stable enough to survive a reboot.</summary>
    public required string DeviceName { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Target capture rate; <c>null</c> means follow the display's own refresh rate.</summary>
    public int? Fps { get; init; }

    public int BitrateMbps { get; init; } = 40;

    /// <summary>Friendly label used in the UI and in output filenames.</summary>
    public string? Label { get; init; }
}

public sealed record AudioTrackConfig
{
    public required string Name { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Linear gain applied when mixing this track. 1.0 is unity.</summary>
    public double Gain { get; init; } = 1.0;

    /// <summary>Raw source strings; see <see cref="AudioSourceSpec"/> for the grammar.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<AudioSourceSpec> ParsedSources => Sources.Select(AudioSourceSpec.Parse);
}
