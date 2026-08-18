using ReplayCapture.Core;
using ReplayCapture.Core.Capture;
using ReplayCapture.Core.Encoders;

namespace ReplayCapture.Tests;

/// <summary>
/// Covers two related resilience mechanisms in <see cref="DisplayRecorder{TFrame}"/>: a native
/// resolution change no longer tears the encoder down or discards the ring buffer (the encode size
/// is fixed for the recorder's lifetime and the change is absorbed by
/// <see cref="IVideoEncoder{TFrame}.NotifySourceResized"/> instead), and a display with nothing to
/// offer — no frame yet, or temporarily unavailable — gets a solid black frame instead of the tick
/// being skipped outright.
/// </summary>
public class DisplayRecorderResilienceTests
{
    private static DisplayInfo MakeDisplay(int width, int height) => new()
    {
        DeviceName = @"\\.\DISPLAY1",
        MonitorHandle = 1,
        AdapterDescription = "Fake Adapter",
        Left = 0,
        Top = 0,
        Width = width,
        Height = height,
        RefreshHz = 60,
        IsPrimary = true,
    };

    [Fact]
    public void A_native_resolution_change_rescales_the_encoder_without_discarding_the_buffer()
    {
        var capture = new FakeCaptureSource(new FrameSize(1920, 1080));
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(1920, 1080), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (_, _) => encoder);

        // Seed the ring buffer as if a frame had already been encoded, so a discard is observable.
        encoder.RaisePacket([1, 2, 3, 4], frameIndex: 0, qpcTicks: 0, isKeyframe: true);
        var bufferedBefore = recorder.BufferedBytes;
        Assert.True(bufferedBefore > 0);

        // The display switches to a new native resolution.
        capture.Resize(new FrameSize(2560, 1440));
        Assert.True(recorder.ResizeRequested);

        recorder.ApplyResize();

        Assert.Equal(1, recorder.Resizes);
        Assert.Equal(1, capture.RecreateCalls);
        Assert.Equal(2560, encoder.LastNotifiedWidth);
        Assert.Equal(1440, encoder.LastNotifiedHeight);

        // The whole point: the encode size and the buffered history are untouched by the resize.
        Assert.Equal(1920, recorder.Width);
        Assert.Equal(1080, recorder.Height);
        Assert.Equal(bufferedBefore, recorder.BufferedBytes);
        Assert.False(encoder.WasDisposed);
    }

    [Fact]
    public void A_second_resize_landing_mid_recreate_is_reflected_by_the_settled_size_not_the_stale_request()
    {
        var capture = new FakeCaptureSource(new FrameSize(1920, 1080));
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(1920, 1080), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (_, _) => encoder);

        capture.Resize(new FrameSize(2560, 1440));
        // A second change lands while Recreate() (standing in for a slow real re-acquire) is still
        // settling on the first one.
        capture.SettleAtInsteadOnNextRecreate = new FrameSize(3840, 2160);

        recorder.ApplyResize();

        // The encoder must be told what capture actually settled at, not the stale pre-Recreate read.
        Assert.Equal(3840, encoder.LastNotifiedWidth);
        Assert.Equal(2160, encoder.LastNotifiedHeight);
    }

    [Fact]
    public void RequestResize_drives_the_same_path_a_real_content_size_change_does()
    {
        var capture = new FakeCaptureSource(new FrameSize(1920, 1080));
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(1920, 1080), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (_, _) => encoder);

        recorder.RequestResize();
        recorder.ApplyResize();

        Assert.Equal(1, recorder.Resizes);
        Assert.Equal(1920, encoder.LastNotifiedWidth);
        Assert.Equal(1080, encoder.LastNotifiedHeight);
    }

    [Fact]
    public void A_display_with_no_signal_gets_a_black_frame_instead_of_a_skipped_tick()
    {
        var capture = new FakeCaptureSource(new FrameSize(1920, 1080)) { HasSignal = false };
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(1920, 1080), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (_, _) => encoder);

        recorder.OnTick(frameIndex: 0, scheduledQpc: 1000);

        Assert.Equal(1, encoder.FramesEncoded);
        Assert.Same(capture.BlackFrame, encoder.LastEncodedFrame);
        Assert.Equal(1, recorder.BlankFrames);
    }

    [Fact]
    public void Signal_returning_stops_the_black_frames_and_encodes_the_real_one_again()
    {
        var capture = new FakeCaptureSource(new FrameSize(1920, 1080)) { HasSignal = false };
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(1920, 1080), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (_, _) => encoder);

        recorder.OnTick(frameIndex: 0, scheduledQpc: 1000);
        Assert.Equal(1, recorder.BlankFrames);

        capture.HasSignal = true;
        recorder.OnTick(frameIndex: 1, scheduledQpc: 2000);

        Assert.NotSame(capture.BlackFrame, encoder.LastEncodedFrame);
        Assert.Equal(1, recorder.BlankFrames);   // unchanged: this tick had a real frame
        Assert.Equal(2, encoder.FramesEncoded);
    }

    [Fact]
    public void A_configured_encode_size_overrides_the_displays_native_size_from_the_start()
    {
        var capture = new FakeCaptureSource(new FrameSize(2560, 1440));
        var encoder = new FakeEncoder(1920, 1080);

        using var recorder = new DisplayRecorder<object>(
            MakeDisplay(2560, 1440), framesPerSecond: 60, bufferSeconds: 10, memoryLimitBytes: 1024 * 1024,
            captureFactory: () => capture,
            encoderFactory: (w, h) =>
            {
                // The encoder factory should be asked to build at the configured size, not the
                // display's actual native size.
                Assert.Equal(1920, w);
                Assert.Equal(1080, h);
                return encoder;
            },
            fixedEncodeSize: new FrameSize(1920, 1080));

        Assert.Equal(1920, recorder.Width);
        Assert.Equal(1080, recorder.Height);

        // The scaler must be told about the real native size immediately, without waiting for a
        // resolution change that may never come.
        Assert.Equal(2560, encoder.LastNotifiedWidth);
        Assert.Equal(1440, encoder.LastNotifiedHeight);
    }

    private sealed class FakeCaptureSource(FrameSize initialSize) : IDisplayCaptureSource<object>
    {
        public DisplayInfo Display { get; } = MakeDisplay(initialSize.Width, initialSize.Height);
        public bool IsClosed => false;
        public FrameSize ContentSize { get; private set; } = initialSize;
        public event Action<FrameSize>? ContentSizeChanged;
        public long FramesArrived => 0;
        public int RecreateCalls { get; private set; }

        /// <summary>
        /// When set, the next <see cref="Recreate"/> call settles at this size instead of the one
        /// it was asked for — simulating a second resolution change landing while a real backend's
        /// re-acquire (which can block for a while) is still in flight.
        /// </summary>
        public FrameSize? SettleAtInsteadOnNextRecreate { get; set; }

        /// <summary>When false, <see cref="TryGetLatest"/> reports nothing available, as if the display had no signal.</summary>
        public bool HasSignal { get; set; } = true;

        public object BlackFrame { get; } = new();

        public bool TryGetLatest(out object frame, out long qpcTicks)
        {
            if (!HasSignal)
            {
                frame = null!;
                qpcTicks = 0;
                return false;
            }

            frame = new object();
            qpcTicks = 0;
            return true;
        }

        public void Recreate(FrameSize size)
        {
            RecreateCalls++;
            ContentSize = SettleAtInsteadOnNextRecreate ?? size;
            SettleAtInsteadOnNextRecreate = null;
        }

        /// <summary>Simulates the backend detecting a native resolution change on its own thread.</summary>
        public void Resize(FrameSize newSize)
        {
            ContentSize = newSize;
            ContentSizeChanged?.Invoke(newSize);
        }

        public void Dispose() { }
    }

    private sealed class FakeEncoder(int width, int height) : IVideoEncoder<object>
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int FramesPerSecond => 60;
        public long TicksPerFrame => 0;
        public byte[] ExtraData => [];
        public long FramesEncoded { get; private set; }
        public long BytesProduced => 0;
        public bool WasDisposed { get; private set; }
        public int LastNotifiedWidth { get; private set; }
        public int LastNotifiedHeight { get; private set; }
        public object? LastEncodedFrame { get; private set; }

        public event Action<ReadOnlySpan<byte>, long, long, bool>? PacketReady;

        public void Encode(object source, long frameIndex, long qpcTicks, bool forceKeyframe = false)
        {
            LastEncodedFrame = source;
            FramesEncoded++;
        }

        public void NotifySourceResized(int newWidth, int newHeight)
        {
            LastNotifiedWidth = newWidth;
            LastNotifiedHeight = newHeight;
        }

        public void Flush() { }

        public void RaisePacket(byte[] data, long frameIndex, long qpcTicks, bool isKeyframe) =>
            PacketReady?.Invoke(data, frameIndex, qpcTicks, isKeyframe);

        public void Dispose() => WasDisposed = true;
    }
}
