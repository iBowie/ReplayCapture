using System.Runtime.InteropServices;
using ReplayCapture.Core.Capture.Linux.Interop;
using ReplayCapture.Core.Diagnostics;
using ReplayCapture.Core.Timing;

namespace ReplayCapture.Core.Capture.Linux;

/// <summary>
/// Captures frames from the PipeWire node a <see cref="Portal.ScreenCastPortalSession"/> handed
/// back, and keeps the most recent one latched — the same "just hold the newest frame, let
/// <see cref="FramePacer"/> turn irregular arrivals into constant rate" shape as the Windows capture
/// sources.
/// <para>
/// Deliberately implements <see cref="IDisplayCaptureSource{TFrame}"/> over
/// <see cref="PipeWireBuffer"/>, not <see cref="VaapiFrame"/>: this class can only produce what
/// PipeWire itself hands back (a buffer that may or may not be DMA-BUF-backed, per
/// <see cref="DmaBufImporter"/>'s remarks). Turning that into a <see cref="VaapiFrame"/> needs a real
/// <c>VaapiContext</c> (Linux support plan Phase 2, not written yet) — that will be a small adapter
/// (<c>IDisplayCaptureSource&lt;PipeWireBuffer&gt;</c> → <c>IDisplayCaptureSource&lt;VaapiFrame&gt;</c>)
/// wrapping an instance of this class, not a change to this class itself. This is exactly the seam
/// <see cref="IDisplayCaptureSource{TFrame}"/> being generic (Linux support plan Phase 1) was meant
/// to make possible.
/// </para>
/// <para>
/// <b>Draft, unverified against any real PipeWire runtime or compositor</b> — see
/// <see cref="Interop.PipeWireInterop"/> and <see cref="Interop.SpaPodBuilder"/> for the specific,
/// higher-risk parts of what this class depends on.
/// </para>
/// </summary>
public sealed unsafe class PipeWireStream : IDisplayCaptureSource<PipeWireBuffer>
{
    private readonly nint _threadLoop;
    private readonly nint _context;
    private readonly nint _core;
    private readonly nint _stream;
    private readonly nint _eventsPtr;
    private readonly nint _hookPtr;
    private readonly GCHandle _selfHandle;
    private readonly int _pipeWireFd;
    private readonly uint _nodeId;

    private readonly Lock _latchGate = new();
    private PipeWireBuffer _latch;
    private bool _hasLatch;
    private long _latchQpc;
    private long _framesArrived;
    private bool _disposed;
    private volatile bool _closed;

    public DisplayInfo Display { get; }
    public bool IsClosed => _closed;
    public FrameSize ContentSize { get; private set; }
    public event Action<FrameSize>? ContentSizeChanged;
    public long FramesArrived => Interlocked.Read(ref _framesArrived);

    /// <param name="pipeWireFd">The fd from the portal's <c>OpenPipeWireRemote</c> call.</param>
    /// <param name="nodeId">The PipeWire node id from the portal's <c>Start</c> response.</param>
    public PipeWireStream(DisplayInfo display, int pipeWireFd, uint nodeId, FrameSize initialSize)
    {
        Display = display;
        ContentSize = initialSize;
        _pipeWireFd = pipeWireFd;
        _nodeId = nodeId;

        PipeWireInterop.pw_init(0, 0);

        _threadLoop = PipeWireInterop.pw_thread_loop_new("replaycapture-capture", 0);
        if (_threadLoop == 0) throw new InvalidOperationException("pw_thread_loop_new failed.");

        var loop = PipeWireInterop.pw_thread_loop_get_loop(_threadLoop);
        _context = PipeWireInterop.pw_context_new(loop, 0, 0);
        if (_context == 0) throw new InvalidOperationException("pw_context_new failed.");

        if (PipeWireInterop.pw_thread_loop_start(_threadLoop) != 0)
            throw new InvalidOperationException("pw_thread_loop_start failed.");

        _selfHandle = GCHandle.Alloc(this);

        PipeWireInterop.pw_thread_loop_lock(_threadLoop);
        try
        {
            _core = PipeWireInterop.pw_context_connect_fd(_context, _pipeWireFd, 0, 0);
            if (_core == 0) throw new InvalidOperationException("pw_context_connect_fd failed.");

            _stream = PipeWireInterop.pw_stream_new(_core, $"replaycapture-{display.DeviceName}", 0);
            if (_stream == 0) throw new InvalidOperationException("pw_stream_new failed.");

            _eventsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PipeWireInterop.PwStreamEvents>());
            var events = new PipeWireInterop.PwStreamEvents
            {
                Version = PipeWireInterop.PwVersionStreamEvents,
                Process = (nint)(delegate* unmanaged<nint, void>)&OnProcess,
                StateChanged = (nint)(delegate* unmanaged<nint, int, int, nint, void>)&OnStateChanged,
            };
            Marshal.StructureToPtr(events, _eventsPtr, fDeleteOld: false);

            // spa_hook's real size is unverified — PipeWire treats it as an opaque node it links
            // into its own internal list, so this only needs to be "big enough," but exactly how big
            // needs checking against the real struct, not assumed.
            _hookPtr = Marshal.AllocHGlobal(128);

            PipeWireInterop.pw_stream_add_listener(_stream, _hookPtr, _eventsPtr, GCHandle.ToIntPtr(_selfHandle));

            Connect(initialSize);
        }
        finally
        {
            PipeWireInterop.pw_thread_loop_unlock(_threadLoop);
        }

        Log.Info($"PipeWire capture started for {display.DeviceName} at {initialSize.Width}x{initialSize.Height} (node {nodeId}).");
    }

    /// <summary>Must be called with the thread loop lock held.</summary>
    private void Connect(FrameSize size)
    {
        var podBytes = SpaPodBuilder.BuildFixedVideoFormat(size.Width, size.Height, Math.Max(Display.RefreshHz, 1), 1);

        fixed (byte* pod = podBytes)
        {
            var paramsArray = stackalloc nint[1];
            paramsArray[0] = (nint)pod;

            var result = PipeWireInterop.pw_stream_connect(
                _stream, PipeWireInterop.PwDirection.Input, _nodeId,
                PipeWireInterop.PwStreamFlags.Autoconnect | PipeWireInterop.PwStreamFlags.MapBuffers | PipeWireInterop.PwStreamFlags.RtProcess,
                (nint)paramsArray, 1);

            if (result < 0) throw new InvalidOperationException($"pw_stream_connect failed: {result}");
        }
    }

    [UnmanagedCallersOnly]
    private static void OnProcess(nint data)
    {
        // Never let an exception cross back into native code from here — PipeWire calls this
        // directly from its own realtime thread, and an unhandled managed exception unwinding into
        // native code is undefined behavior, not a normal crash-and-log situation.
        try
        {
            if (GCHandle.FromIntPtr(data).Target is PipeWireStream self) self.HandleProcess();
        }
        catch (Exception ex)
        {
            Log.Error("PipeWire process callback failed", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnStateChanged(nint data, int oldState, int newState, nint error)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is PipeWireStream self) self.HandleStateChanged(newState);
        }
        catch (Exception ex)
        {
            Log.Error("PipeWire state_changed callback failed", ex);
        }
    }

    private void HandleStateChanged(int newState)
    {
        var state = (PipeWireInterop.PwStreamState)newState;
        if (state is PipeWireInterop.PwStreamState.Error or PipeWireInterop.PwStreamState.Unconnected)
        {
            _closed = true;
            Log.Warn($"PipeWire stream for {Display.DeviceName} entered state {state}; treating capture as closed.");
        }
    }

    private void HandleProcess()
    {
        var bufPtr = PipeWireInterop.pw_stream_dequeue_buffer(_stream);
        if (bufPtr == 0) return;

        try
        {
            var pwBuffer = Marshal.PtrToStructure<PipeWireInterop.PwBuffer>(bufPtr);
            if (pwBuffer.Buffer == 0) return;

            var spaBuffer = Marshal.PtrToStructure<PipeWireInterop.SpaBuffer>(pwBuffer.Buffer);
            if (spaBuffer.NDatas == 0 || spaBuffer.Datas == 0) return;

            // Only the first plane: correct for the single-plane packed format
            // Interop.SpaPodBuilder currently requests. A planar format would need every plane.
            var spaData = Marshal.PtrToStructure<PipeWireInterop.SpaData>(spaBuffer.Datas);
            var chunk = spaData.Chunk != 0
                ? Marshal.PtrToStructure<PipeWireInterop.SpaChunk>(spaData.Chunk)
                : default;

            var buffer = new PipeWireBuffer(
                spaData.Type,
                (int)spaData.Fd,
                chunk.Offset,
                chunk.Size,
                chunk.Stride,
                spaData.Data,
                ContentSize.Width,
                ContentSize.Height);

            lock (_latchGate)
            {
                _latch = buffer;
                _hasLatch = true;
                _latchQpc = Clock.Now;
            }

            Interlocked.Increment(ref _framesArrived);
        }
        finally
        {
            // Requeues immediately. A DMA-BUF fd survives this (the importer takes its own reference
            // during vaCreateSurfaces before this runs again), but a MemPtr/MemFd buffer's contents
            // do not — TryGetLatest's caller must finish reading before the next Process callback
            // reuses this slot, same "no time to spare" constraint the Windows sources' double-buffer
            // latch exists to avoid. This class does not yet solve that for the copy path; revisit
            // once DmaBufImporter's DMA-BUF-vs-MemFd finding (see its remarks) is known.
            PipeWireInterop.pw_stream_queue_buffer(_stream, bufPtr);
        }
    }

    public bool TryGetLatest(out PipeWireBuffer frame, out long qpcTicks)
    {
        lock (_latchGate)
        {
            if (!_hasLatch)
            {
                frame = default;
                qpcTicks = 0;
                return false;
            }

            frame = _latch;
            qpcTicks = _latchQpc;
            return true;
        }
    }

    /// <summary>
    /// Not implemented: unlike the Windows backends, a <see cref="PipeWireBuffer"/> wraps a real
    /// DMA-BUF/shared-memory handle the compositor owns, not a texture this class can allocate and
    /// clear itself — synthesizing one needs a real buffer allocator (Linux support plan Phase 2,
    /// alongside the VAAPI wiring), which doesn't exist yet. Not reachable today: this class isn't
    /// wired into any <c>DisplayRecorder</c> yet either (see the class remarks).
    /// </summary>
    public PipeWireBuffer BlackFrame =>
        throw new NotSupportedException(
            "PipeWireStream cannot synthesize a black frame yet — see the Linux support plan.");

    /// <summary>
    /// Reconnects the stream at a new size. Best-effort and unverified: a portal-backed ScreenCast
    /// session may need a fresh portal negotiation on a resolution change rather than a plain stream
    /// reconnect — see the Linux support plan's Phase 3 notes on this being an open question for the
    /// App-shell integration, not something Core can resolve alone.
    /// </summary>
    public void Recreate(FrameSize size)
    {
        PipeWireInterop.pw_thread_loop_lock(_threadLoop);
        try
        {
            ContentSize = size;
            Connect(size);
        }
        finally
        {
            PipeWireInterop.pw_thread_loop_unlock(_threadLoop);
        }

        lock (_latchGate) _hasLatch = false;
        ContentSizeChanged?.Invoke(size);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadLoop != 0) PipeWireInterop.pw_thread_loop_stop(_threadLoop);
        if (_stream != 0) PipeWireInterop.pw_stream_destroy(_stream);
        if (_core != 0) PipeWireInterop.pw_core_disconnect(_core);
        if (_context != 0) PipeWireInterop.pw_context_destroy(_context);
        if (_threadLoop != 0) PipeWireInterop.pw_thread_loop_destroy(_threadLoop);

        if (_eventsPtr != 0) Marshal.FreeHGlobal(_eventsPtr);
        if (_hookPtr != 0) Marshal.FreeHGlobal(_hookPtr);
        if (_selfHandle.IsAllocated) _selfHandle.Free();

        Log.Info($"PipeWire capture stopped for {Display.DeviceName} after {FramesArrived} frames.");
    }
}
