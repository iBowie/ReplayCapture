using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ReplayCapture.Core.Audio.Interop;
using ReplayCapture.Core.Diagnostics;
using Sdcb.FFmpeg.Raw;
using Windows.Win32.Media.Audio;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Captures the audio of a single process (and its child processes) — the mechanism behind separate
/// Game, Communications and Music stems.
/// <para>
/// This uses the process-loopback virtual device introduced in Windows 10 build 20348. Unlike an
/// ordinary endpoint it is not activated through <c>IMMDevice</c>: it is activated by name through
/// <c>ActivateAudioInterfaceAsync</c>, with the target process id passed in a PROPVARIANT blob, and
/// the resulting <c>IAudioClient</c> arrives on a COM callback rather than as a return value.
/// </para>
/// </summary>
public sealed unsafe partial class ProcessLoopbackSource : IAudioSource
{
    /// <summary>Device interface path of the process-loopback virtual device.</summary>
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    private const long BufferDurationTicks = 20 * TimeSpan.TicksPerMillisecond;
    private const ushort VtBlob = 65;

    private readonly WasapiCaptureLoop _loop;

    public string Name { get; }
    public uint ProcessId { get; }
    public string ExecutableName { get; }

    public long FramesCaptured => _loop.FramesCaptured;

    public event Action<long, ReadOnlyMemory<float>>? SamplesReady;

    public ProcessLoopbackSource(uint processId, string executableName, bool includeProcessTree = true)
    {
        ProcessId = processId;
        ExecutableName = executableName;
        Name = $"{executableName}#{processId}";

        var client = ActivateProcessLoopbackClient(processId, includeProcessTree);

        // The virtual device has no mix format to query — GetMixFormat is meaningless here — so the
        // format has to be stated outright and the device converts to it.
        var format = new WAVEFORMATEX
        {
            wFormatTag = 1,                       // WAVE_FORMAT_PCM
            nChannels = AudioFormat.Channels,
            nSamplesPerSec = AudioFormat.SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = AudioFormat.Channels * 2,
            nAvgBytesPerSec = AudioFormat.SampleRate * AudioFormat.Channels * 2,
            cbSize = 0,
        };

        client.Initialize(
            AudclntSharemode.Shared,
            Constants.StreamflagsLoopback | Constants.StreamflagsEventcallback,
            BufferDurationTicks,
            0,
            (nint)(&format),
            null);

        var captureIid = typeof(IAudioCaptureClient).GUID;
        client.GetService(captureIid, out var capturePtr);

        var resampler = new AudioResampler(AudioFormat.SampleRate, AudioFormat.Channels, AVSampleFormat.S16);

        _loop = new WasapiCaptureLoop(
            client, ComInterop.WrapAndRelease<IAudioCaptureClient>(capturePtr), resampler, Name,
            (qpc, samples) => SamplesReady?.Invoke(qpc, samples));

        Log.Info($"Process loopback attached to {Name}.");
    }

    private static IAudioClient ActivateProcessLoopbackClient(uint processId, bool includeTree)
    {
        var activationParams = new AudioclientActivationParams
        {
            ActivationType = 1,   // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = processId,
            ProcessLoopbackMode = includeTree ? 0u : 1u,
        };

        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<AudioclientActivationParams>());
        var propVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());

        try
        {
            Marshal.StructureToPtr(activationParams, blob, false);
            Marshal.StructureToPtr(
                new PropVariantBlob
                {
                    vt = VtBlob,
                    cbSize = (uint)Marshal.SizeOf<AudioclientActivationParams>(),
                    pBlobData = blob,
                },
                propVariant, false);

            // ActivateAudioInterfaceAsync completes on an MTA thread. Driving it from the thread
            // pool (which is MTA) keeps this correct even when called from the WPF UI thread, which
            // is STA and would otherwise deadlock waiting on a callback it is blocking.
            var completion = new ActivationHandler();
            var task = Task.Run(() =>
            {
                var audioClientIid = typeof(IAudioClient).GUID;
                // LibraryImport-generated stubs always preserve the native signature, unlike classic
                // DllImport's PreserveSig=false — the HRESULT has to be checked explicitly here.
                Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                    VirtualAudioDeviceProcessLoopback,
                    in audioClientIid,
                    propVariant,
                    completion,
                    out _));

                if (!completion.Completed.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("ActivateAudioInterfaceAsync did not complete within 5s.");

                if (completion.HResult < 0)
                    Marshal.ThrowExceptionForHR(completion.HResult);

                return completion.Interface!;
            });

            return task.GetAwaiter().GetResult();
        }
        finally
        {
            Marshal.FreeHGlobal(propVariant);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Start() => _loop.Start();

    public void Dispose() => _loop.Dispose();

    // --- Interop that CsWin32 cannot supply ------------------------------------------------
    // CsWin32 emits IActivateAudioInterfaceCompletionHandler and IActivateAudioInterfaceAsyncOperation
    // as *empty* interfaces, so they are declared by hand here as [GeneratedComInterface]s — the
    // source-generated, ComWrappers-based interop that Native AOT requires in place of classic
    // [ComImport]. The handler in particular has to be implemented in managed code and handed to
    // native code, which is the direction [GeneratedComClass] (below) exists for.

    [LibraryImport("Mmdevapi.dll")]
    private static partial int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [GeneratedComInterface]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    internal partial interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [GeneratedComInterface]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    internal partial interface IActivateAudioInterfaceAsyncOperation
    {
        // The activated interface comes back as a raw pointer rather than a wrapped object: the new
        // source-generated COM interop has no equivalent of classic interop's dynamic
        // [MarshalAs(UnmanagedType.IUnknown)] "give me whatever COM type this is" marshaling, which
        // Native AOT cannot support (it needs the concrete interface known at compile time). We
        // requested IAudioClient specifically via the riid passed to ActivateAudioInterfaceAsync, so
        // the caller knows what the pointer actually is.
        void GetActivateResult(out int activateResult, out nint activatedInterface);
    }

    /// <summary>Receives the activated <c>IAudioClient</c> on a COM callback thread.</summary>
    [GeneratedComClass]
    private sealed partial class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new(false);
        public int HResult { get; private set; }
        public IAudioClient? Interface { get; private set; }

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var result, out var activatedInterface);
                HResult = result;
                if (activatedInterface != 0)
                    Interface = ComInterop.WrapAndRelease<IAudioClient>(activatedInterface);
            }
            catch (Exception ex)
            {
                HResult = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
        }
    }

    /// <summary>Layout-compatible with AUDIOCLIENT_ACTIVATION_PARAMS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioclientActivationParams
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    /// <summary>A PROPVARIANT holding a VT_BLOB. 24 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public uint cbSize;
        public uint padding;
        public IntPtr pBlobData;
    }
}
