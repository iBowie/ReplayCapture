using System.Runtime.InteropServices;

// ReSharper disable once RedundantUsingDirective -- referenced only from XML doc <see cref>s below.
using ReplayCapture.Core.Capture.Linux;

namespace ReplayCapture.Core.Capture.Linux.Interop;

/// <summary>
/// Hand-written P/Invoke surface against <c>libpipewire-0.3.so</c> — there is no .NET PipeWire
/// binding to build on (verified: no such NuGet package exists), so this is authored from memory of
/// the public <c>pipewire/pipewire.h</c> / <c>pipewire/stream.h</c> headers, not checked against the
/// actual headers or run against a real <c>libpipewire</c>.
/// <para>
/// <b>Everything in this file is an unverified draft.</b> The specific, most dangerous risk is
/// <see cref="PwStreamEvents"/>: it is a native struct of callback function pointers PipeWire invokes
/// directly, and its field count/order has changed across PipeWire 0.3.x minor versions. A layout
/// mismatch here does not throw — it corrupts memory or invokes the wrong callback silently. Before
/// this is used for anything beyond reading, generate (or hand-check) this struct against
/// <c>pkg-config --cflags libpipewire-0.3</c>'s actual header on the exact target distro/version, and
/// prefer testing each callback individually (e.g. log from <c>state_changed</c> alone first) over
/// trusting the whole vtable at once.
/// </para>
/// </summary>
internal static unsafe partial class PipeWireInterop
{
    private const string PipeWire = "libpipewire-0.3.so.0";

    [LibraryImport(PipeWire)]
    public static partial void pw_init(nint argc, nint argv);

    [LibraryImport(PipeWire)]
    public static partial void pw_deinit();

    [LibraryImport(PipeWire, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_thread_loop_new(string? name, nint props);

    [LibraryImport(PipeWire)]
    public static partial int pw_thread_loop_start(nint loop);

    [LibraryImport(PipeWire)]
    public static partial void pw_thread_loop_stop(nint loop);

    [LibraryImport(PipeWire)]
    public static partial void pw_thread_loop_destroy(nint loop);

    [LibraryImport(PipeWire)]
    public static partial void pw_thread_loop_lock(nint loop);

    [LibraryImport(PipeWire)]
    public static partial void pw_thread_loop_unlock(nint loop);

    [LibraryImport(PipeWire)]
    public static partial nint pw_thread_loop_get_loop(nint loop);

    [LibraryImport(PipeWire)]
    public static partial nint pw_context_new(nint mainLoop, nint props, nuint userDataSize);

    [LibraryImport(PipeWire)]
    public static partial void pw_context_destroy(nint context);

    [LibraryImport(PipeWire)]
    public static partial int pw_core_disconnect(nint core);

    /// <summary>
    /// Connects to the compositor's own PipeWire instance using the fd the portal handed back from
    /// <c>OpenPipeWireRemote</c> — this, not a named socket, is how a sandboxed/unprivileged app is
    /// allowed to reach the specific remote the user already consented to in the portal picker.
    /// </summary>
    [LibraryImport(PipeWire)]
    public static partial nint pw_context_connect_fd(nint context, int fd, nint properties, nuint userDataSize);

    [LibraryImport(PipeWire, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_stream_new(nint core, string name, nint props);

    [LibraryImport(PipeWire)]
    public static partial void pw_stream_destroy(nint stream);

    [LibraryImport(PipeWire)]
    public static partial int pw_stream_connect(
        nint stream, PwDirection direction, uint targetId, PwStreamFlags flags,
        nint /* const spa_pod** */ @params, uint nParams);

    [LibraryImport(PipeWire)]
    public static partial void pw_stream_add_listener(nint stream, nint listener, nint events, nint data);

    [LibraryImport(PipeWire)]
    public static partial nint pw_stream_dequeue_buffer(nint stream);

    [LibraryImport(PipeWire)]
    public static partial int pw_stream_queue_buffer(nint stream, nint buffer);

    public enum PwDirection
    {
        Input = 0,
        Output = 1,
    }

    [Flags]
    public enum PwStreamFlags : uint
    {
        None = 0,
        Autoconnect = 1 << 0,
        Inactive = 1 << 1,
        MapBuffers = 1 << 2,
        DontReconnect = 1 << 3,
        RtProcess = 1 << 5,
    }

    /// <summary>
    /// <c>struct pw_stream_events</c> — DRAFT, unverified layout (see the type-level remarks). The
    /// first field is always <c>uint32_t version</c> (<c>PW_VERSION_STREAM_EVENTS</c>) per PipeWire's
    /// versioning convention for every <c>*_events</c> struct; every field after it is a function
    /// pointer, in the declaration order from <c>pipewire/stream.h</c> as of the 0.3 series this was
    /// written against memory of. Only <see cref="ProcessCallback"/> is expected to be wired up by
    /// <see cref="PipeWireStream"/> initially — everything else can be left null while that one
    /// callback alone is validated against a real stream first.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PwStreamEvents
    {
        public uint Version;
        public nint Destroy;
        public nint StateChanged;
        public nint ControlInfo;
        public nint IoChanged;
        public nint ParamChanged;
        public nint AddBuffer;
        public nint RemoveBuffer;
        public nint Process;
        public nint Drained;
        public nint Command;
        public nint TriggerDone;
    }

    public const uint PwVersionStreamEvents = 2;

    public delegate void ProcessCallback(nint data);
    public delegate void ParamChangedCallback(nint data, uint id, nint param);
    public delegate void StateChangedCallback(nint data, int old, int state, nint error);

    /// <summary>
    /// <c>enum pw_stream_state</c> (pipewire/stream.h) — DRAFT, unverified numeric values, written
    /// from memory. <see cref="Error"/> and <see cref="Unconnected"/> are what
    /// <see cref="PipeWireStream"/> treats as "closed."
    /// </summary>
    public enum PwStreamState
    {
        Error = -1,
        Unconnected = 0,
        Connecting = 1,
        Configure = 2,
        Ready = 3,
        Paused = 4,
        Streaming = 5,
    }

    /// <summary><c>struct spa_chunk</c> (spa/buffer/buffer.h) — DRAFT, unverified field order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }

    /// <summary>
    /// <c>struct spa_data</c> (spa/buffer/buffer.h) — DRAFT, unverified field order/size. The real
    /// struct's <c>fd</c> field is a 64-bit union member even though only the low bits are ever a
    /// meaningful fd; kept as <see cref="long"/> here to preserve its width.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaData
    {
        public SpaDataType Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public nint Data;
        public nint Chunk; // SpaChunk*
    }

    /// <summary><c>struct spa_buffer</c> (spa/buffer/buffer.h) — DRAFT, unverified field order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaBuffer
    {
        public uint NMetas;
        public uint NDatas;
        public nint Metas;
        public nint Datas; // SpaData*
    }

    /// <summary><c>struct pw_buffer</c> (pipewire/stream.h) — DRAFT, unverified field order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PwBuffer
    {
        public nint Buffer; // SpaBuffer*
        public nint UserData;
        public ulong Size;
        public ulong Requested;
    }
}
