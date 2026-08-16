using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ReplayCapture.Core.Capture;

/// <summary>
/// Draws the Desktop Duplication cursor onto a captured frame with a GPU alpha-blend quad, instead
/// of reading the destination back to the CPU and blending there.
/// <para>
/// A CPU read-modify-write was tried first and is the textbook-correct approach (Desktop
/// Duplication's monochrome and masked-color cursors both have a genuine per-pixel "invert" mode
/// that only makes sense against the real destination pixel). It measurably stalled the whole
/// pipeline: mapping a region for CPU read/write, even one tick after it was copied, still
/// serializes against the encoder's own GPU work through the driver-protected
/// <see cref="ID3D11DeviceContext"/> every display shares, and cost 150+ late pacer ticks in 20s at
/// a 60fps target. A blend-only draw touches no CPU memory in the per-frame path — only a
/// <c>WriteDiscard</c> vertex-buffer update (never stalls, by design) and a <c>Draw</c> call — so it
/// costs nothing next to the desktop copy already happening every tick.
/// </para>
/// <para>
/// The trade-off: a real blend can't reproduce the "invert" pixel (monochrome AND=1,XOR=1, or a
/// nonzero masked-color mask byte), since that needs to read the destination and this draw never
/// does. Those pixels render transparent instead. That combination is a legacy trick from
/// pre-cursor-theme Windows and essentially never appears in a modern arrow, hand, or I-beam cursor
/// — every ordinary opaque/transparent pixel, which is what those actually use, renders exactly right.
/// </para>
/// </summary>
internal sealed class CursorOverlay : IDisposable
{
    private const uint PointerShapeTypeMonochrome = 1;
    private const uint PointerShapeTypeColor = 2;
    private const uint PointerShapeTypeMaskedColor = 4;

    private readonly D3DContext _d3d;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _vertexBuffer;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11SamplerState _samplerState;

    private byte[]? _convertedSprite;
    private ID3D11Texture2D? _spriteTexture;
    private ID3D11ShaderResourceView? _spriteView;
    private int _spriteWidth;
    private int _spriteHeight;
    private bool _hasSprite;

    private ID3D11Texture2D? _targetForRtv;
    private ID3D11RenderTargetView? _targetRtv;

    public CursorOverlay(D3DContext d3d)
    {
        _d3d = d3d;

        var vsBytecode = LoadShaderBytecode("CursorBlitVS.cso");
        var psBytecode = LoadShaderBytecode("CursorBlitPS.cso");

        _vertexShader = d3d.Device.CreateVertexShader(vsBytecode);
        _pixelShader = d3d.Device.CreatePixelShader(psBytecode);

        _inputLayout = d3d.Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
        ], vsBytecode);

        _vertexBuffer = d3d.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 4 * 4 * sizeof(float), // 4 verts * (float2 pos + float2 uv)
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        var blendDescription = new BlendDescription();
        blendDescription.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = d3d.Device.CreateBlendState(blendDescription);

        _samplerState = d3d.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipPoint,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            TextureAddressMode.Clamp,
            0f,
            1,
            ComparisonFunction.Never,
            0f,
            float.MaxValue));
    }

    private static byte[] LoadShaderBytecode(string name)
    {
        var assembly = typeof(CursorOverlay).Assembly;
        using var stream = assembly.GetManifestResourceStream($"ReplayCapture.Core.Capture.Shaders.{name}")
            ?? throw new InvalidOperationException($"Embedded shader resource '{name}' not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Rebuilds the sprite texture from raw Desktop Duplication shape data. Call only when
    /// <c>OutduplFrameInfo.PointerShapeBufferSize</c> is nonzero — shape changes far less often than
    /// position, so most ticks skip this entirely.
    /// </summary>
    public unsafe void UpdateShape(byte* shapeBuffer, in OutduplPointerShapeInfo info)
    {
        var isMono = info.Type == PointerShapeTypeMonochrome;
        var width = (int)info.Width;
        var height = isMono ? (int)info.Height / 2 : (int)info.Height;
        if (width <= 0 || height <= 0)
        {
            _hasSprite = false;
            return;
        }

        var byteCount = width * height * 4;
        if (_convertedSprite is null || _convertedSprite.Length < byteCount)
            _convertedSprite = new byte[byteCount];

        fixed (byte* destBase = _convertedSprite)
        {
            for (var y = 0; y < height; y++)
            {
                var destRow = (uint*)(destBase + y * width * 4);
                for (var x = 0; x < width; x++)
                    destRow[x] = ConvertPixel(shapeBuffer, in info, x, y);
            }

            EnsureSpriteTexture(width, height);
            _d3d.ImmediateContext.UpdateSubresource(_spriteTexture!, 0, null, (IntPtr)destBase, (uint)(width * 4), 0);
        }

        _hasSprite = true;
    }

    /// <summary>Converts one shape pixel to straight BGRA. See the class remarks for the invert trade-off.</summary>
    private static unsafe uint ConvertPixel(byte* shapeBase, in OutduplPointerShapeInfo info, int x, int y)
    {
        switch (info.Type)
        {
            case PointerShapeTypeColor:
                return *(uint*)(shapeBase + y * (int)info.Pitch + x * 4); // already straight BGRA

            case PointerShapeTypeMaskedColor:
            {
                var offset = y * (int)info.Pitch + x * 4;
                var b = shapeBase[offset]; var g = shapeBase[offset + 1]; var r = shapeBase[offset + 2]; var mask = shapeBase[offset + 3];
                return mask == 0 ? 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b : 0u;
            }

            default: // Monochrome
            {
                var byteIndex = x / 8;
                var bitMask = (byte)(0x80 >> (x % 8));
                var andByte = shapeBase[y * (int)info.Pitch + byteIndex];
                var xorRowOffset = ((int)info.Height / 2 + y) * (int)info.Pitch;
                var xorByte = shapeBase[xorRowOffset + byteIndex];

                var and = (andByte & bitMask) != 0;
                var xor = (xorByte & bitMask) != 0;

                if (and) return 0u; // transparent, and the unreproducible invert case both land here
                return xor ? 0xFFFFFFFFu : 0xFF000000u; // opaque white / opaque black
            }
        }
    }

    private void EnsureSpriteTexture(int width, int height)
    {
        if (_spriteTexture is not null && _spriteWidth == width && _spriteHeight == height) return;

        _spriteView?.Dispose();
        _spriteTexture?.Dispose();

        _spriteWidth = width;
        _spriteHeight = height;
        _spriteTexture = _d3d.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _spriteView = _d3d.Device.CreateShaderResourceView(_spriteTexture);
    }

    /// <summary>
    /// Draws the current sprite at <paramref name="left"/>,<paramref name="top"/> (target-local
    /// pixels — may run negative or past the target's edge; the viewport clips it) onto
    /// <paramref name="target"/>, recording onto <paramref name="context"/> — typically the caller's
    /// own deferred context, not the shared immediate context, so building this draw call never
    /// contends with anything else touching the device.
    /// </summary>
    public void Draw(ID3D11DeviceContext context, ID3D11Texture2D target, int targetWidth, int targetHeight, int left, int top)
    {
        if (!_hasSprite) return;

        EnsureRenderTargetView(target);

        var ndcLeft = left / (float)targetWidth * 2f - 1f;
        var ndcRight = (left + _spriteWidth) / (float)targetWidth * 2f - 1f;
        var ndcTop = 1f - top / (float)targetHeight * 2f;
        var ndcBottom = 1f - (top + _spriteHeight) / (float)targetHeight * 2f;

        Span<float> vertices =
        [
            ndcLeft, ndcTop, 0f, 0f,
            ndcRight, ndcTop, 1f, 0f,
            ndcLeft, ndcBottom, 0f, 1f,
            ndcRight, ndcBottom, 1f, 1f,
        ];

        // WriteDiscard never waits for the GPU - the driver just hands back a fresh chunk of the
        // buffer - unlike the Map(ReadWrite) the CPU-blend approach needed.
        var mapped = context.Map(_vertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (float* src = vertices)
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer, vertices.Length * sizeof(float), vertices.Length * sizeof(float));
        }
        context.Unmap(_vertexBuffer, 0);

        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _vertexBuffer, 4 * sizeof(float));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, _spriteView!);
        context.PSSetSampler(0, _samplerState);
        context.OMSetRenderTargets(_targetRtv!);
        context.OMSetBlendState(_blendState);
        context.RSSetViewport(0, 0, targetWidth, targetHeight);

        context.Draw(4, 0);

        // Leave the target free to be bound elsewhere (e.g. as the video processor's input) right
        // after this call returns.
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        context.PSSetShaderResource(0, null!);
    }

    private void EnsureRenderTargetView(ID3D11Texture2D target)
    {
        if (ReferenceEquals(_targetForRtv, target) && _targetRtv is not null) return;

        _targetRtv?.Dispose();
        _targetForRtv = target;
        _targetRtv = _d3d.Device.CreateRenderTargetView(target);
    }

    public void Dispose()
    {
        _targetRtv?.Dispose();
        _spriteView?.Dispose();
        _spriteTexture?.Dispose();
        _vertexBuffer.Dispose();
        _samplerState.Dispose();
        _blendState.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }
}
