using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Diagnostics;

Log.MinimumLevel = LogLevel.Debug;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "displays":
        ListDisplays();
        break;

    case "api":
        DumpApi(args.Length > 1 ? args[1] : "Video");
        break;

    case "capture":
        CaptureSmokeTest(args.Length > 1 ? double.Parse(args[1]) : 3.0);
        break;

    case "record":
        Record(
            args.Length > 1 ? int.Parse(args[1]) : 10,
            args.Length > 2 ? args[2] : @"S:\_replayCapture\out");
        break;

    case "audio":
        ListAudioEndpoints();
        break;

    case "sessions":
        ListAudioSessions();
        break;

    case "bench":
        Benchmark(args.Length > 1 ? int.Parse(args[1]) : 20);
        break;

    case "rebuild":
        RebuildTest();
        break;

    default:
        Console.WriteLine("""
            rcprobe — ReplayCapture developer diagnostics

              displays          Enumerate attached displays as the capture pipeline sees them
              audio             Enumerate active audio endpoints
              sessions          Show processes holding audio sessions and the track each maps to
              capture [secs]    Capture the primary display and convert frames to NV12
              record [secs] [outDir]
                                Run the full pipeline and write one .mov per display
              bench [secs]      Measure the always-on overhead against an idle baseline
              rebuild           Force an encoder rebuild mid-capture and check recovery
            """);
        break;
}

// Scaffolding aid: prints the shape of Vortice's video-processor surface so the converter can be
// written against the real API instead of guesses.
static void DumpApi(string filter)
{
    var assembly = typeof(Vortice.Direct3D11.ID3D11Device).Assembly;
    foreach (var type in assembly.GetExportedTypes()
                 .Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(t => t.Name))
    {
        Console.WriteLine($"\n=== {type.FullName} ({(type.IsEnum ? "enum" : type.IsInterface ? "interface" : "struct/class")}) ===");

        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type)) Console.WriteLine($"    {name}");
            continue;
        }

        foreach (var member in type.GetMethods(System.Reflection.BindingFlags.Public
                                               | System.Reflection.BindingFlags.Instance
                                               | System.Reflection.BindingFlags.DeclaredOnly))
        {
            var parameters = string.Join(", ", member.GetParameters()
                .Select(p => $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{p.ParameterType.Name.TrimEnd('&')} {p.Name}"));
            Console.WriteLine($"    {member.ReturnType.Name} {member.Name}({parameters})");
        }

        foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public
                                             | System.Reflection.BindingFlags.Instance))
        {
            Console.WriteLine($"    .{field.Name} : {field.FieldType.Name}");
        }
    }
}

// The whole pipeline in one command: capture -> pace -> NVENC -> ring, plus every audio track,
// written out as one .mov per display.
static void Record(int seconds, string outputDirectory)
{
    var config = new ReplayCapture.Core.Config.AppConfig
    {
        BufferSeconds = seconds,
        OutputDirectory = outputDirectory,
    };

    using var session = new ReplayCapture.Core.ReplaySession(config);

    Console.WriteLine($"\nRecording {session.Recorders.Count} display(s) and " +
                      $"{session.Audio.Tracks.Count} audio track(s) for {seconds}s…\n");

    foreach (var track in session.Audio.Tracks)
        Console.WriteLine($"    track: {track.Name}");
    Console.WriteLine();

    session.Start();

    var started = DateTime.UtcNow;
    while ((DateTime.UtcNow - started).TotalSeconds < seconds + 1)
    {
        Thread.Sleep(1000);
        var recorder = session.Recorders[0];
        Console.Write($"\r  buffered {recorder.SecondsBuffered,5:0.0}s  " +
                      $"{recorder.BufferedBytes / (1024 * 1024),4} MB video  " +
                      $"{session.Audio.TotalBytes / (1024 * 1024),4} MB audio  " +
                      $"encoded {recorder.FramesEncoded,5}  " +
                      $"dup {recorder.DuplicateFrames,5}  " +
                      $"late {recorder.LateTicks,3}");
    }

    Console.WriteLine("\n");
    foreach (var result in session.Save())
    {
        Console.WriteLine(result.Success
            ? $"  WROTE {result.Path}\n    duration {result.DurationSeconds:0.00}s, {result.Bytes / (1024 * 1024)} MB"
            : $"  FAILED: {result.Error}");
    }

    foreach (var track in session.Audio.Tracks)
        Console.WriteLine($"    {track.Name,-16} {track.FramesAccumulated,9} frames accumulated");

    Console.WriteLine("\n  process bindings:");
    foreach (var (track, processes) in session.Audio.DescribeProcessBindings())
    {
        var attached = processes.ToList();
        Console.WriteLine($"    {track,-16} {(attached.Count > 0 ? string.Join(", ", attached) : "(none attached)")}");
    }

    Console.WriteLine();
}

// End-to-end check of the GPU path up to (but not including) the encoder: WGC delivers frames,
// the latch survives pool recycling, and the video processor produces NV12.
static void CaptureSmokeTest(double seconds)
{
    var display = DisplayEnumerator.Enumerate().FirstOrDefault(d => d.IsPrimary)
                  ?? DisplayEnumerator.Enumerate().First();

    Console.WriteLine($"\nCapturing {display.DeviceName} for {seconds:0.#}s…\n");

    using var d3d = new D3DContext();
    using var capture = new DisplayCaptureSource(d3d, display);

    var size = capture.ContentSize;
    using var converter = new Nv12Converter(d3d, size.Width, size.Height, display.RefreshHz);

    // Stand-in for the encoder's frame pool: a single-slice NV12 array.
    using var nv12 = d3d.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
    {
        Width = (uint)size.Width,
        Height = (uint)size.Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Vortice.DXGI.Format.NV12,
        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
        Usage = Vortice.Direct3D11.ResourceUsage.Default,
        BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget,
        CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None,
        MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
    });

    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    int converted = 0, missed = 0;
    long firstQpc = 0, lastQpc = 0;

    while (DateTime.UtcNow < deadline)
    {
        if (capture.TryGetLatest(out var texture, out var qpc))
        {
            converter.Convert(texture, nv12, 0);
            converted++;
            if (firstQpc == 0) firstQpc = qpc;
            lastQpc = qpc;
        }
        else
        {
            missed++;
        }

        Thread.Sleep(1000 / Math.Max(display.RefreshHz, 1));
    }

    d3d.ImmediateContext.Flush();

    Console.WriteLine($"  frames delivered by WGC : {capture.FramesArrived}");
    Console.WriteLine($"  NV12 conversions done   : {converted}");
    Console.WriteLine($"  ticks with no frame yet : {missed}");
    Console.WriteLine($"  capture timestamp span  : {ReplayCapture.Core.Timing.Clock.ToSeconds(lastQpc - firstQpc):0.000}s");
    Console.WriteLine($"  device lost             : {d3d.IsDeviceLost}");
    Console.WriteLine(converted > 0 && !d3d.IsDeviceLost
        ? "\n  PASS - capture and NV12 conversion are working.\n"
        : "\n  FAIL - no frames converted.\n");
}

// Exercises the recovery path a resolution change takes: recreate the frame pool, rebuild the
// encoder, discard the ring, and keep going. Triggered deliberately so the risky code is tested
// without changing anyone's display settings.
static void RebuildTest()
{
    var config = new ReplayCapture.Core.Config.AppConfig { BufferSeconds = 10 };
    using var session = new ReplayCapture.Core.ReplaySession(config);
    session.Start();

    var recorder = session.Recorders[0];
    Console.WriteLine("\nBuffering for 6s before forcing a rebuild…\n");
    Thread.Sleep(6000);

    var beforeFrames = recorder.FramesEncoded;
    var beforeBuffered = recorder.SecondsBuffered;
    Console.WriteLine($"  before   {beforeFrames,6} frames  {beforeBuffered,5:F1}s buffered  " +
                      $"{recorder.Rebuilds} rebuild(s)");

    recorder.RequestRebuild();
    Thread.Sleep(1500);

    var afterRebuild = recorder.FramesEncoded;
    Console.WriteLine($"  rebuilt  {afterRebuild,6} frames  {recorder.SecondsBuffered,5:F1}s buffered  " +
                      $"{recorder.Rebuilds} rebuild(s)   <- buffer intentionally discarded");

    Console.WriteLine("\nRefilling for 8s…\n");
    Thread.Sleep(8000);

    var recovered = recorder.FramesEncoded - afterRebuild;
    Console.WriteLine($"  after    {recorder.FramesEncoded,6} frames  {recorder.SecondsBuffered,5:F1}s buffered  " +
                      $"late ticks {recorder.LateTicks}");

    var results = session.Save();
    var saved = results.FirstOrDefault();

    var pass = recorder.Rebuilds == 1
               && recovered > 300               // encoding resumed at roughly full rate
               && saved.Success
               && saved.DurationSeconds > 5;

    Console.WriteLine();
    Console.WriteLine(saved.Success
        ? $"  post-rebuild save: {saved.Path} ({saved.DurationSeconds:F2}s)"
        : $"  post-rebuild save FAILED: {saved.Error}");

    Console.WriteLine(pass
        ? "\n  PASS - the pipeline recovered and still produces a valid clip.\n"
        : "\n  FAIL - recovery did not complete cleanly.\n");
}

// Measures what the always-on buffer actually costs. The buffer runs continuously from logon, so
// idle overhead is the number that matters — not peak throughput.
static void Benchmark(int seconds)
{
    Console.WriteLine("\nMeasuring idle baseline (no capture)…");
    var baseline = SampleGpu(TimeSpan.FromSeconds(5));

    var config = new ReplayCapture.Core.Config.AppConfig { BufferSeconds = 30 };
    using var session = new ReplayCapture.Core.ReplaySession(config);
    session.Start();

    Console.WriteLine($"Warming up, then sampling for {seconds}s…\n");
    Thread.Sleep(3000);

    var process = System.Diagnostics.Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var recorder = session.Recorders[0];

    // Count only the frames produced inside the sample window; the warm-up frames would otherwise
    // make the pipeline look like it over-produced.
    var framesBefore = recorder.FramesEncoded;
    var duplicatesBefore = recorder.DuplicateFrames;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    var armed = SampleGpu(TimeSpan.FromSeconds(seconds));

    stopwatch.Stop();
    process.Refresh();
    var cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
    var cpuPercent = cpuSeconds / stopwatch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100.0;

    var framesEncoded = recorder.FramesEncoded - framesBefore;
    var duplicates = recorder.DuplicateFrames - duplicatesBefore;
    var expectedFrames = stopwatch.Elapsed.TotalSeconds * recorder.FramesPerSecond;

    Console.WriteLine("  GPU");
    Console.WriteLine($"    utilisation idle      {baseline.GpuUtilisation,6:F1} %");
    Console.WriteLine($"    utilisation armed     {armed.GpuUtilisation,6:F1} %");
    Console.WriteLine($"    delta                 {armed.GpuUtilisation - baseline.GpuUtilisation,6:F1} %");
    Console.WriteLine($"    encoder sessions      {armed.EncoderSessions,6:F1}");
    Console.WriteLine($"    encoder fps           {armed.EncoderFps,6:F1}");
    Console.WriteLine($"    encoder latency       {armed.EncoderLatencyUs,6:F0} us");
    Console.WriteLine();
    Console.WriteLine("  Process");
    Console.WriteLine($"    CPU                   {cpuPercent,6:F2} % of all cores");
    Console.WriteLine($"    working set           {process.WorkingSet64 / (1024 * 1024),6} MB");
    Console.WriteLine($"    ring buffers          {session.BufferedBytes / (1024 * 1024),6} MB");
    Console.WriteLine();
    Console.WriteLine("  Pipeline");
    Console.WriteLine($"    displays              {session.Recorders.Count,6}");
    Console.WriteLine($"    audio tracks          {session.Audio.Tracks.Count,6}");
    Console.WriteLine($"    frames encoded        {framesEncoded,6}  (expected ~{expectedFrames:F0} at {recorder.FramesPerSecond} fps)");
    Console.WriteLine($"    frame rate accuracy   {100.0 * framesEncoded / expectedFrames,6:F2} %");
    Console.WriteLine($"    duplicate frames      {duplicates,6}  (unchanged screen; near-free P-frames)");
    Console.WriteLine($"    late pacer ticks      {recorder.LateTicks,6}");
    Console.WriteLine($"    seconds buffered      {recorder.SecondsBuffered,6:F1}");
    Console.WriteLine();
}

static GpuSample SampleGpu(TimeSpan duration)
{
    var util = new List<double>();
    var sessions = new List<double>();
    var fps = new List<double>();
    var latency = new List<double>();

    var deadline = DateTime.UtcNow + duration;
    while (DateTime.UtcNow < deadline)
    {
        var line = RunNvidiaSmi(
            "--query-gpu=utilization.gpu,encoder.stats.sessionCount,encoder.stats.averageFps,encoder.stats.averageLatency " +
            "--format=csv,noheader,nounits");

        var parts = line?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is { Length: >= 4 })
        {
            if (double.TryParse(parts[0], out var u)) util.Add(u);
            if (double.TryParse(parts[1], out var s)) sessions.Add(s);
            if (double.TryParse(parts[2], out var f)) fps.Add(f);
            if (double.TryParse(parts[3], out var l)) latency.Add(l);
        }

        Thread.Sleep(500);
    }

    static double Mean(List<double> values) => values.Count == 0 ? double.NaN : values.Average();
    return new GpuSample(Mean(util), Mean(sessions), Mean(fps), Mean(latency));
}

static string? RunNvidiaSmi(string arguments)
{
    try
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nvidia-smi", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });

        var output = process!.StandardOutput.ReadLine();
        process.WaitForExit(5000);
        return output;
    }
    catch
    {
        // No NVIDIA tooling on PATH; the process-side numbers are still worth having.
        return null;
    }
}

// Shows the rules engine's decision for every process currently making sound, without starting a
// capture — the quickest way to check a config change did what you meant.
static void ListAudioSessions()
{
    var sessions = ReplayCapture.Core.Audio.AudioSessionMonitor.ListActiveSessions();

    // Read the *live* config, not the shipped defaults - the whole point is checking your own edits.
    var config = new ReplayCapture.Core.Config.ConfigStore().Load();
    var tracks = config.AudioTracks;

    Console.WriteLine($"\nUsing {ReplayCapture.Core.Config.ConfigStore.FilePath}");
    Console.WriteLine($"{sessions.Count} process(es) holding an audio session:\n");

    foreach (var session in sessions.OrderBy(s => s.ExecutableName))
    {
        var matched = tracks
            .Where(t => t.Enabled)
            .Where(t =>
            {
                var process = t.ParsedSources.Where(s => s.Kind == ReplayCapture.Core.Config.AudioSourceKind.Process).ToList();
                var includes = process.Where(s => !s.IsExclusion);
                var excludes = process.Where(s => s.IsExclusion);
                return includes.Any(s => s.MatchesProcess(session.ExecutableName))
                       && !excludes.Any(s => s.MatchesProcess(session.ExecutableName));
            })
            .Select(t => t.Name)
            .ToList();

        var verdict = matched.Count switch
        {
            0 => "(no process track)",
            1 => matched[0],
            // Landing on two tracks means the same audio is duplicated across stems, which is
            // almost always a missing exclusion rather than an intent.
            _ => $"{string.Join(", ", matched)}   <-- DUPLICATED across {matched.Count} tracks",
        };

        Console.WriteLine($"  {session.ExecutableName,-28} pid {session.ProcessId,-8} -> {verdict}");
    }

    // Static check across the whole config, not just what happens to be running right now.
    Console.WriteLine("\n  config check:");
    var named = tracks
        .SelectMany(t => t.ParsedSources
            .Where(s => s.Kind == ReplayCapture.Core.Config.AudioSourceKind.Process
                        && !s.IsExclusion && s.ProcessPattern != "*")
            .Select(s => (Track: t.Name, Pattern: s.ProcessPattern!)))
        .ToList();

    var problems = 0;
    foreach (var (track, pattern) in named)
    {
        foreach (var other in tracks.Where(t => t.Name != track))
        {
            var otherSpecs = other.ParsedSources.Where(s => s.Kind == ReplayCapture.Core.Config.AudioSourceKind.Process).ToList();
            var catchAll = otherSpecs.Any(s => !s.IsExclusion && s.ProcessPattern == "*");
            if (!catchAll) continue;

            if (!otherSpecs.Any(s => s.IsExclusion && s.MatchesProcess(pattern)))
            {
                Console.WriteLine($"    '{pattern}' is on '{track}' but not excluded from '{other.Name}' (proc:*)");
                problems++;
            }
        }
    }

    Console.WriteLine(problems == 0
        ? "    ok - no app can land on two tracks at once"
        : $"    {problems} problem(s) - add the missing proc:! exclusions");

    Console.WriteLine();
}

static void ListAudioEndpoints()
{
    var endpoints = ReplayCapture.Core.Audio.AudioDeviceEnumerator.List();
    Console.WriteLine($"\n{endpoints.Count} active endpoint(s):\n");

    foreach (var group in endpoints.GroupBy(e => e.IsRender))
    {
        Console.WriteLine($"  {(group.Key ? "Playback (captured as loopback)" : "Recording")}:");
        foreach (var endpoint in group)
        {
            Console.WriteLine($"    {(endpoint.IsDefault ? "*" : " ")} {endpoint.FriendlyName}");
            Console.WriteLine($"        {endpoint.Id}");
        }

        Console.WriteLine();
    }
}

static void ListDisplays()
{
    var displays = DisplayEnumerator.Enumerate();
    Console.WriteLine($"\n{displays.Count} display(s):\n");

    foreach (var d in displays)
    {
        Console.WriteLine($"  {d.DeviceName}{(d.IsPrimary ? "  [primary]" : "")}");
        Console.WriteLine($"    resolution : {d.Width}x{d.Height} @ {d.RefreshHz} Hz");
        Console.WriteLine($"    position   : ({d.Left}, {d.Top})");
        Console.WriteLine($"    adapter    : {d.AdapterDescription}");
        Console.WriteLine($"    hmonitor   : 0x{d.MonitorHandle:X}");
        Console.WriteLine();
    }
}

// Type declarations must follow all top-level statements.
readonly record struct GpuSample(double GpuUtilisation, double EncoderSessions, double EncoderFps, double EncoderLatencyUs);
