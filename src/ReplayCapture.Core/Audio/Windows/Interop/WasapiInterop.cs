using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ReplayCapture.Core.Audio.Interop;

// Hand-authored, source-generated (ComWrappers-based) replacements for the WASAPI/MMDevice COM
// interfaces CsWin32 would otherwise generate as classic [ComImport] interop, which Native AOT does
// not support. Method order within each interface matches the real native vtable exactly (verified
// against CsWin32's own [ComImport]-based generated output before this rewrite, including each
// interface's inherited base-interface methods). Methods this codebase never calls are still
// declared — with simplified, param-free placeholder signatures — purely to occupy their real
// vtable slot, so the methods actually called land at the correct offset. Getting a slot wrong here
// silently corrupts calls rather than failing loudly, so do not reorder or remove members without
// re-deriving the real vtable order.
//
// Every enum/struct parameter below is a LOCAL type, not the equivalent Windows.Win32.* type CsWin32
// generates: empirically, [GeneratedComInterface]'s ComInterfaceGenerator cannot validate blittability
// for a type produced by a DIFFERENT source generator in the same compilation — it reports SYSLIB1051
// ("not supported by source-generated COM") even for a plain int-backed enum, while an identical
// locally-declared enum works fine, with or without DisableRuntimeMarshalling. Cast to/from the real
// Windows.Win32.* type at each call site — the underlying values always match (verified against the
// real Win32 constants below).

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
internal enum DeviceState : uint { Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8 }
internal enum AudclntSharemode { Shared = 0, Exclusive = 1 }
internal enum Clsctx : uint { All = 0x17 }
internal enum Stgm : uint { Read = 0 }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
}

/// <summary>
/// Minimal PROPVARIANT layout: an 8-byte header (vt + 3 reserved ushorts) plus 16 bytes of trailing
/// payload, matching the real PROPVARIANT's total size on x64. This codebase only ever reads a
/// single pointer out of the union (a VT_LPWSTR string), so the real ~40-member nested union isn't
/// modeled here — <see cref="System.Runtime.CompilerServices.Unsafe.As{TFrom,TTo}(ref TFrom)"/>
/// bridges back to the real <c>Windows.Win32.System.Com.StructuredStorage.PROPVARIANT</c> (identical
/// layout) for <c>PropVariantClear</c> and field access, both of which are unaffected by the
/// cross-generator issue above since they're plain CsWin32 P/Invoke, not COM interop.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort reserved1;
    public ushort reserved2;
    public ushort reserved3;
    public nint value;
    public nint padding;
}

/// <summary>
/// Marker interface with no members: declares an object "agile" (free-threaded), so COM can call it
/// back directly from any apartment without marshaling through a proxy. Any callback object exposed
/// to native code from this codebase (an <see cref="IMMNotificationClient"/> or
/// <c>IActivateAudioInterfaceCompletionHandler</c> implementation) must also implement this — the
/// source-generated COM interop has no implicit agility of its own, unlike classic COM interop, and
/// without it native callbacks fail with E_ILLEGAL_METHOD_CALL (0x8000000E) the moment they arrive on
/// a different thread than the one that registered the callback.
/// </summary>
[GeneratedComInterface]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
internal partial interface IAgileObject;

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    void RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    // Real signature hands back whichever interface `iid` names — classic interop marshaled that
    // dynamically as `object`, which Native AOT's source-generated interop cannot do (the concrete
    // type must be known at compile time). Every call site here already knows exactly which concrete
    // interface it asked for via `iid`, so the raw pointer is bridged with
    // Interop.ComInterop.WrapAndRelease<T> instead.
    void Activate(in Guid iid, Clsctx dwClsCtx, nint pActivationParams, out nint ppInterface);
    void OpenPropertyStore(Stgm stgmAccess, out IPropertyStore ppProperties);
    void GetId(out nint ppstrId);
    void GetState(out DeviceState pdwState); // placeholder — never called
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    void GetCount(out uint pcDevices);
    void Item(uint nDevice, out IMMDevice ppDevice);
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    // PCWSTR params are raw pointers (nint): PCWSTR is itself a CsWin32-generated single-pointer
    // wrapper struct, so it hits the same cross-generator issue as the enums above — nint has the
    // identical memory layout.
    void OnDeviceStateChanged(nint pwstrDeviceId, DeviceState dwNewState);
    void OnDeviceAdded(nint pwstrDeviceId);
    void OnDeviceRemoved(nint pwstrDeviceId);
    void OnDefaultDeviceChanged(EDataFlow flow, ERole role, nint pwstrDefaultDeviceId);
    void OnPropertyValueChanged(nint pwstrDeviceId, PropertyKey key);
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal unsafe partial interface IAudioClient
{
    void Initialize(AudclntSharemode shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat, Guid* audioSessionGuid);
    void GetBufferSize(out uint pNumBufferFrames);
    void GetStreamLatency(out long phnsLatency);
    void GetCurrentPadding(out uint pNumPaddingFrames);
    void IsFormatSupported(); // placeholder — never called
    void GetMixFormat(out nint ppDeviceFormat);
    void GetDevicePeriod(); // placeholder — never called
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(nint eventHandle);
    // Real signature dynamically hands back whichever service `riid` names — same AOT limitation as
    // IMMDevice.Activate above; bridged the same way via WrapAndRelease<T>.
    void GetService(in Guid riid, out nint ppv);
}

[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal unsafe partial interface IAudioCaptureClient
{
    void GetBuffer(out nint ppData, out uint pNumFramesToRead, out uint pdwFlags, ulong* pu64DevicePosition, ulong* pu64QPCPosition);
    void ReleaseBuffer(uint numFramesRead);
    void GetNextPacketSize(out uint pNumFramesInNextPacket);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    void GetBuffer(uint numFramesRequested, out nint ppData);
    void ReleaseBuffer(uint numFramesWritten, uint dwFlags);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    void GetCount(out uint cProps); // placeholder — never called
    void GetAt(uint iProp, out PropertyKey pkey); // placeholder — never called
    void GetValue(in PropertyKey key, out PropVariant pv);
    void SetValue(in PropertyKey key, in PropVariant propvar); // placeholder — never called
    void Commit(); // placeholder — never called
}

/// <summary>
/// Flattened: the real interface derives from <c>IAudioSessionManager</c> (2 methods, slots 0-1,
/// never called here) before adding its own 5. <see cref="GetSessionEnumerator"/> is the only member
/// this codebase uses.
/// </summary>
[GeneratedComInterface]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2
{
    void GetAudioSessionControl(); // placeholder (base IAudioSessionManager member) — never called
    void GetSimpleAudioVolume(); // placeholder (base IAudioSessionManager member) — never called
    IAudioSessionEnumerator GetSessionEnumerator();
    void RegisterSessionNotification(); // placeholder — never called
    void UnregisterSessionNotification(); // placeholder — never called
    void RegisterDuckNotification(); // placeholder — never called
    void UnregisterDuckNotification(); // placeholder — never called
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    void GetCount(out int SessionCount);

    // Real signature returns IAudioSessionControl (the base interface) — every session control this
    // API can ever return is documented to also implement IAudioSessionControl2, which is the only
    // one this codebase needs, so the raw pointer is wrapped directly as that concrete type instead
    // of modeling the unused base interface at all.
    void GetSession(int SessionCount, out nint Session);
}

/// <summary>
/// Flattened: the real interface derives from <c>IAudioSessionControl</c> (9 methods, slots 0-8,
/// never called here) before adding its own 5. <see cref="GetProcessId"/> is the only member this
/// codebase uses.
/// </summary>
[GeneratedComInterface]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
internal partial interface IAudioSessionControl2
{
    void GetState(); // 0 base placeholder
    void GetDisplayName(); // 1 base placeholder
    void SetDisplayName(); // 2 base placeholder
    void GetIconPath(); // 3 base placeholder
    void SetIconPath(); // 4 base placeholder
    void GetGroupingParam(); // 5 base placeholder
    void SetGroupingParam(); // 6 base placeholder
    void RegisterAudioSessionNotification(); // 7 base placeholder
    void UnregisterAudioSessionNotification(); // 8 base placeholder
    void GetSessionIdentifier(); // 9 placeholder
    void GetSessionInstanceIdentifier(); // 10 placeholder
    void GetProcessId(out uint pRetVal);
    void IsSystemSoundsSession(); // 12 placeholder
    void SetDuckingPreference(); // 13 placeholder
}

/// <summary>Bridges raw COM pointers to our <c>[GeneratedComInterface]</c> types.</summary>
internal static class ComInterop
{
    /// <summary>
    /// Wraps a raw COM interface pointer obtained from an "out"-style native parameter (Activate,
    /// GetService, CoCreateInstance, an enumerator's GetXxx — anything following COM's universal
    /// "out interface pointer" convention) as the given <c>[GeneratedComInterface]</c>-attributed
    /// type, then releases the reference the out-parameter itself handed us:
    /// <see cref="ComInterfaceMarshaller{T}.ConvertToManaged"/> takes its own independent reference
    /// when building the managed wrapper, so the original one would otherwise leak.
    /// </summary>
    internal static unsafe T WrapAndRelease<T>(nint ptr) where T : class
    {
        var managed = ComInterfaceMarshaller<T>.ConvertToManaged((void*)ptr)
            ?? throw new InvalidOperationException($"Received a null {typeof(T).Name} pointer.");
        Marshal.Release(ptr);
        return managed;
    }
}
