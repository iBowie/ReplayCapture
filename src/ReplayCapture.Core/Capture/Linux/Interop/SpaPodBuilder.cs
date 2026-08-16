using System.Runtime.InteropServices;

namespace ReplayCapture.Core.Capture.Linux.Interop;

/// <summary>
/// Builds the one SPA POD <see cref="PipeWireStream"/> needs: a <c>SPA_TYPE_OBJECT_Format</c>
/// requesting a fixed-size <c>video/raw</c> format, passed to <c>pw_stream_connect</c>'s
/// <c>params</c> array so the compositor knows what to offer.
/// <para>
/// <b>The least trustworthy file in this draft.</b> SPA's POD binary format (every value is an
/// 8-byte-aligned <c>{ size, type }</c> header followed by its body; an Object POD nests a sequence
/// of <c>spa_pod_prop</c> entries) is normally built with C macros
/// (<c>spa_pod_builder_add_object</c>, <c>SPA_FORMAT_VideoFormat</c>, ...) this file hand-expands
/// from memory with no header to check against. It also only offers a single fixed format/size —
/// real negotiation should offer a <c>SPA_CHOICE_Enum</c> of acceptable formats and a
/// <c>SPA_CHOICE_Range</c> of acceptable sizes, which is a meaningfully larger POD shape than this
/// covers. Treat this as "enough bytes to see what <c>pw_stream_connect</c> and the compositor do
/// with a plausible-looking POD," not a correct implementation — expect to rewrite it once it can be
/// tested against a real <c>spa_debug_pod</c> dump.
/// </para>
/// </summary>
internal static class SpaPodBuilder
{
    // SPA type ids (spa/utils/type.h) relevant here.
    private const uint SpaTypeObject = 0x10004;
    private const uint SpaTypeId = 0x6;
    private const uint SpaTypeRectangle = 0x9;
    private const uint SpaTypeFraction = 0xa;

    // SPA_TYPE_OBJECT_Format (spa/param/param.h).
    private const uint SpaTypeObjectFormat = 0x40002;

    // SPA_PARAM_EnumFormat (spa/param/param.h).
    private const uint SpaParamEnumFormat = 1;

    // Format property keys (spa/param/format.h / format-utils.h).
    private const uint SpaFormatMediaType = 1;
    private const uint SpaFormatMediaSubtype = 2;
    private const uint SpaFormatVideoFormat = 0x10001;
    private const uint SpaFormatVideoSize = 0x10003;
    private const uint SpaFormatVideoFramerate = 0x10004;

    // spa_media_type / spa_media_subtype (spa/param/format.h).
    private const int SpaMediaTypeVideo = 2;
    private const int SpaMediaSubtypeRaw = 1;

    // spa_video_format (spa/param/video/raw.h) — BGRx, the format the Windows backends already
    // normalize to before NV12 conversion, chosen so the eventual VAAPI VPP step has one fewer
    // format to branch on. Real negotiation should offer several and let the compositor pick.
    private const int SpaVideoFormatBgrx = 15;

    /// <summary>
    /// Builds one fixed-format <c>video/raw</c> Format object POD as a byte array PipeWire's
    /// <c>const struct spa_pod*</c> array parameter can point at.
    /// </summary>
    public static byte[] BuildFixedVideoFormat(int width, int height, int framerateNumerator, int framerateDenominator)
    {
        using var body = new MemoryStream();

        WriteProp(body, SpaFormatMediaType, WriteId((uint)SpaMediaTypeVideo));
        WriteProp(body, SpaFormatMediaSubtype, WriteId((uint)SpaMediaSubtypeRaw));
        WriteProp(body, SpaFormatVideoFormat, WriteId((uint)SpaVideoFormatBgrx));
        WriteProp(body, SpaFormatVideoSize, WriteRectangle(width, height));
        WriteProp(body, SpaFormatVideoFramerate, WriteFraction(framerateNumerator, framerateDenominator));

        var propsBytes = body.ToArray();

        // Object body: { uint32 object_type; uint32 object_id; } followed by the props.
        using var objectBody = new MemoryStream();
        using (var w = new BinaryWriter(objectBody))
        {
            w.Write(SpaTypeObjectFormat);
            w.Write(SpaParamEnumFormat);
        }
        objectBody.Write(propsBytes);

        return WrapPod(SpaTypeObject, objectBody.ToArray());
    }

    private static byte[] WrapPod(uint type, byte[] body)
    {
        var padded = PadTo8(body);
        using var result = new MemoryStream();
        using (var w = new BinaryWriter(result))
        {
            w.Write((uint)body.Length);
            w.Write(type);
        }
        result.Write(padded);
        return result.ToArray();
    }

    private static byte[] WriteId(uint id) => WrapPod(SpaTypeId, BitConverter.GetBytes(id));

    private static byte[] WriteRectangle(int width, int height)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)width);
        w.Write((uint)height);
        return WrapPod(SpaTypeRectangle, ms.ToArray());
    }

    private static byte[] WriteFraction(int numerator, int denominator)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)numerator);
        w.Write((uint)denominator);
        return WrapPod(SpaTypeFraction, ms.ToArray());
    }

    /// <summary><c>struct spa_pod_prop { uint32 key; uint32 flags; struct spa_pod value; &lt;value body&gt; }</c>.</summary>
    private static void WriteProp(Stream into, uint key, byte[] valuePod)
    {
        using var w = new BinaryWriter(into, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(key);
        w.Write(0u); // flags
        w.Flush();
        into.Write(valuePod);
        var pad = (8 - (int)(into.Position % 8)) % 8;
        if (pad > 0) into.Write(new byte[pad]);
    }

    private static byte[] PadTo8(byte[] data)
    {
        var pad = (8 - data.Length % 8) % 8;
        if (pad == 0) return data;
        var padded = new byte[data.Length + pad];
        Array.Copy(data, padded, data.Length);
        return padded;
    }
}
