using ReplayCapture.Core.Diagnostics;
using Vortice.Direct3D11;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// Converts the BGRA textures WGC produces into the NV12 textures NVENC consumes, using the GPU's
/// fixed-function video processor.
/// <para>
/// A hand-written compute shader would also work, but the video processor is free (it is dedicated
/// silicon, not shader ALUs) and gets the BT.709 matrix and studio-range clamping right by
/// declaration rather than by hand-rolled coefficients.
/// </para>
/// <para>
/// <b>Colour space is load-bearing.</b> Desktop content is full-range RGB; H.264 for delivery is
/// limited-range ("studio", 16-235) BT.709. Getting this pairing wrong produces footage that looks
/// washed out or crushed in Premiere and reads as a capture bug rather than a tagging bug.
/// </para>
/// </summary>
public sealed class Nv12Converter : IDisposable
{
    // D3D11_VIDEO_PROCESSOR_COLOR_SPACE field values, which are plain ints rather than enums.
    private const uint UsagePlaybackNormal = 0;
    private const uint RgbRangeFull = 0;
    private const uint YCbCrMatrixBt709 = 1;
    private const uint NominalRangeStudio = 1;   // 16-235
    private const uint NominalRangeFull = 2;     // 0-255

    private readonly D3DContext _d3d;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;

    // Views are expensive to create and the underlying textures are pooled and reused, so both
    // sides are cached by native pointer.
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inputViews = [];
    private readonly Dictionary<(IntPtr Texture, uint Slice), ID3D11VideoProcessorOutputView> _outputViews = [];

    public int Width { get; }
    public int Height { get; }

    public Nv12Converter(D3DContext d3d, int width, int height, int framesPerSecond)
    {
        _d3d = d3d;
        Width = width;
        Height = height;

        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Vortice.DXGI.Rational((uint)framesPerSecond, 1),
            OutputFrameRate = new Vortice.DXGI.Rational((uint)framesPerSecond, 1),
            Usage = VideoUsage.PlaybackNormal,
        };

        _enumerator = d3d.VideoDevice.CreateVideoProcessorEnumerator(description);
        _processor = d3d.VideoDevice.CreateVideoProcessor(_enumerator, 0);

        // Input: what the desktop actually is - full-range RGB.
        d3d.VideoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = UsagePlaybackNormal,
            RGB_Range = RgbRangeFull,
            Nominal_Range = NominalRangeFull,
        });

        // Output: what H.264 for delivery should be - BT.709, studio range.
        d3d.VideoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = UsagePlaybackNormal,
            YCbCr_Matrix = YCbCrMatrixBt709,
            Nominal_Range = NominalRangeStudio,
        });

        // No deinterlacing, no frame-rate conversion - one input frame in, one output frame out.
        d3d.VideoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        Log.Info($"NV12 converter ready for {width}x{height} (BGRA full-range -> NV12 BT.709 studio).");
    }

    /// <summary>
    /// Converts <paramref name="source"/> into slice <paramref name="destinationSlice"/> of
    /// <paramref name="destination"/>, which is expected to be an NV12 texture array owned by the
    /// encoder's frame pool.
    /// </summary>
    public void Convert(ID3D11Texture2D source, ID3D11Texture2D destination, uint destinationSlice)
    {
        var input = GetInputView(source);
        var output = GetOutputView(destination, destinationSlice);

        VideoProcessorStream[] streams =
        [
            new()
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = input,
            },
        ];

        _d3d.VideoContext.VideoProcessorBlt(_processor, output, 0, 1, streams);
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D texture)
    {
        if (_inputViews.TryGetValue(texture.NativePointer, out var cached)) return cached;

        var view = _d3d.VideoDevice.CreateVideoProcessorInputView(texture, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

        _inputViews[texture.NativePointer] = view;
        return view;
    }

    private ID3D11VideoProcessorOutputView GetOutputView(ID3D11Texture2D texture, uint slice)
    {
        var key = (texture.NativePointer, slice);
        if (_outputViews.TryGetValue(key, out var cached)) return cached;

        // The encoder pool hands out one array slice per in-flight frame, so the view has to target
        // exactly that slice rather than the whole array.
        var view = _d3d.VideoDevice.CreateVideoProcessorOutputView(texture, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayVideoProcessorOutputView
                {
                    MipSlice = 0,
                    FirstArraySlice = slice,
                    ArraySize = 1,
                },
            });

        _outputViews[key] = view;
        return view;
    }

    public void Dispose()
    {
        foreach (var view in _inputViews.Values) view.Dispose();
        foreach (var view in _outputViews.Values) view.Dispose();
        _inputViews.Clear();
        _outputViews.Clear();

        _processor.Dispose();
        _enumerator.Dispose();
    }
}

