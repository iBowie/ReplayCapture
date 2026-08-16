using System.Runtime.InteropServices;
using ReplayCapture.Core.Diagnostics;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Keeps a render endpoint's shared audio engine continuously running by feeding it silence.
/// <para>
/// Loopback capture taps the audio engine's output. When nothing is actually rendering, Windows
/// lets that engine go idle to save power, and the next real sound to start produces an audible
/// pop on the loopback tap as the engine spins back up — a well documented WASAPI loopback quirk
/// that has nothing to do with how the capturing application reads its buffer. Holding open a
/// second, silent render stream on the same endpoint for as long as the loopback capture runs keeps
/// the engine active throughout, so it has no cold start left to glitch on.
/// </para>
/// </summary>
internal sealed unsafe class SilentRenderKeepAlive : IDisposable
{
    private const uint BufferflagsSilent = 0x2;

    private readonly IAudioClient _client;
    private readonly IAudioRenderClient _renderClient;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly uint _bufferFrames;
    private readonly uint _blockAlign;
    private bool _disposed;

    public SilentRenderKeepAlive(IMMDevice device)
    {
        var audioClientIid = typeof(IAudioClient).GUID;
        device.Activate(&audioClientIid, CLSCTX.CLSCTX_ALL, null, out var clientObject);
        _client = (IAudioClient)clientObject;

        WAVEFORMATEX* mixFormat;
        _client.GetMixFormat(&mixFormat);
        try
        {
            _client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                0,
                200 * TimeSpan.TicksPerMillisecond,
                0,
                mixFormat,
                null);
            _blockAlign = mixFormat->nBlockAlign;
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)mixFormat);
        }

        _client.GetBufferSize(out _bufferFrames);

        var renderIid = typeof(IAudioRenderClient).GUID;
        _client.GetService(&renderIid, out var renderObject);
        _renderClient = (IAudioRenderClient)renderObject;

        // Every WASAPI render sample fills the whole buffer with silence before the first Start —
        // starting from empty produces exactly the glitch this class exists to avoid.
        FillSilence(_bufferFrames);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "audio-keepalive",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start()
    {
        _client.Start();
        _thread.Start();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                _client.GetCurrentPadding(out var padding);
                var available = _bufferFrames - padding;
                if (available > 0) FillSilence(available);
            }
            catch (Exception ex)
            {
                Log.Error("Silent keep-alive render failed", ex);
            }

            Thread.Sleep(50);
        }

        try { _client.Stop(); } catch { /* the endpoint may already be gone */ }
    }

    private void FillSilence(uint frames)
    {
        byte* data;
        _renderClient.GetBuffer(frames, &data);
        // GetBuffer's contents are undefined, and not every driver/APO chain actually honors
        // BufferflagsSilent by discarding what's here — some mix the raw bytes in regardless. Zero
        // the buffer explicitly so the packet is real silence even on those drivers; the flag is kept
        // as a hint for the ones that do use it to skip the mix step entirely.
        new Span<byte>(data, checked((int)(frames * _blockAlign))).Clear();
        _renderClient.ReleaseBuffer(frames, BufferflagsSilent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellation.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
    }
}
