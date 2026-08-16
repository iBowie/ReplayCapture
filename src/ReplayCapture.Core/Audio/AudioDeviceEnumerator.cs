using System.Runtime.InteropServices;
using ReplayCapture.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace ReplayCapture.Core.Audio;

/// <summary>One playback or recording endpoint as the user would recognise it.</summary>
public sealed record AudioEndpointInfo
{
    /// <summary>MMDevice id. Stable across reboots, so config can pin a specific device.</summary>
    public required string Id { get; init; }

    public required string FriendlyName { get; init; }

    /// <summary>True for playback devices (captured via loopback), false for microphones.</summary>
    public required bool IsRender { get; init; }

    public required bool IsDefault { get; init; }

    public override string ToString() =>
        $"{FriendlyName} [{(IsRender ? "playback" : "capture")}{(IsDefault ? ", default" : "")}]";
}

/// <summary>
/// Resolves audio endpoints. Keeps the COM interfaces internal so the rest of the app deals in
/// endpoint ids and names rather than in <c>IMMDevice</c>.
/// </summary>
public static partial class AudioDeviceEnumerator
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const uint ClsctxInprocServer = 0x1;

    /// <summary>
    /// PKEY_Device_FriendlyName. CsWin32 does not project this one, so it is spelled out — it is a
    /// documented, fixed property key.
    /// </summary>
    private static readonly Interop.PropertyKey DeviceFriendlyNameKey = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    public static IReadOnlyList<AudioEndpointInfo> List()
    {
        var endpoints = new List<AudioEndpointInfo>();
        var enumerator = CreateEnumerator();

        foreach (var flow in (ReadOnlySpan<EDataFlow>)[EDataFlow.eRender, EDataFlow.eCapture])
        {
            string? defaultId = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint((Interop.EDataFlow)flow, Interop.ERole.eConsole, out var defaultDevice);
                defaultId = GetId(defaultDevice);
            }
            catch (Exception ex)
            {
                Log.Warn($"No default {flow} endpoint: {ex.Message}");
            }

            enumerator.EnumAudioEndpoints((Interop.EDataFlow)flow, Interop.DeviceState.Active, out var collection);
            collection.GetCount(out var count);

            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                var id = GetId(device);

                endpoints.Add(new AudioEndpointInfo
                {
                    Id = id,
                    FriendlyName = GetFriendlyName(device),
                    IsRender = flow == EDataFlow.eRender,
                    IsDefault = id == defaultId,
                });
            }
        }

        return endpoints;
    }

    /// <summary>Opens the default playback device in loopback mode — i.e. "everything you hear".</summary>
    public static WasapiCaptureSource CreateDefaultRenderLoopback(string name = "Desktop")
    {
        CreateEnumerator().GetDefaultAudioEndpoint(Interop.EDataFlow.eRender, Interop.ERole.eConsole, out var device);
        return new WasapiCaptureSource(device, loopback: true, name);
    }

    /// <summary>Opens the default recording device — the microphone.</summary>
    public static WasapiCaptureSource CreateDefaultCapture(string name = "Mic")
    {
        // eCommunications, not eConsole: Windows tracks a separate "the one you talk into" default,
        // and that is the one a user means by "my mic".
        CreateEnumerator().GetDefaultAudioEndpoint(Interop.EDataFlow.eCapture, Interop.ERole.eCommunications, out var device);
        return new WasapiCaptureSource(device, loopback: false, name);
    }

    /// <summary>Opens a specific endpoint by id, for config that pins a device rather than following the default.</summary>
    public static WasapiCaptureSource CreateForEndpoint(string endpointId, bool loopback, string name)
    {
        CreateEnumerator().GetDevice(endpointId, out var device);
        return new WasapiCaptureSource(device, loopback, name);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    /// <summary>Internal so <see cref="DefaultDeviceWatcher"/> can register for endpoint notifications on the same enumerator type.</summary>
    internal static Interop.IMMDeviceEnumerator CreateEnumerator()
    {
        // Classic COM interop's coclass-activation trick (`new MMDeviceEnumerator()`) implicitly
        // calls CoCreateInstance under the hood — a built-in-COM-interop feature Native AOT does not
        // support, so it is done explicitly here instead.
        var iid = typeof(Interop.IMMDeviceEnumerator).GUID;
        Marshal.ThrowExceptionForHR(
            CoCreateInstance(ClsidMMDeviceEnumerator, 0, ClsctxInprocServer, iid, out var ptr));
        return Interop.ComInterop.WrapAndRelease<Interop.IMMDeviceEnumerator>(ptr);
    }

    /// <summary>Opens the session manager for the default playback device, for session enumeration.</summary>
    internal static Interop.IAudioSessionManager2 OpenDefaultRenderSessionManager()
    {
        CreateEnumerator().GetDefaultAudioEndpoint(Interop.EDataFlow.eRender, Interop.ERole.eConsole, out var device);

        var iid = typeof(Interop.IAudioSessionManager2).GUID;
        device.Activate(iid, Interop.Clsctx.All, 0, out var ptr);
        return Interop.ComInterop.WrapAndRelease<Interop.IAudioSessionManager2>(ptr);
    }

    private static unsafe string GetId(Interop.IMMDevice device)
    {
        device.GetId(out var idPtr);
        try
        {
            return Marshal.PtrToStringUni(idPtr) ?? "";
        }
        finally
        {
            PInvoke.CoTaskMemFree((void*)idPtr);
        }
    }

    private static unsafe string GetFriendlyName(Interop.IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(Interop.Stgm.Read, out var store);
            store.GetValue(DeviceFriendlyNameKey, out var rawValue);

            ref var value = ref System.Runtime.CompilerServices.Unsafe.As<Interop.PropVariant, Windows.Win32.System.Com.StructuredStorage.PROPVARIANT>(ref rawValue);
            try
            {
                var name = value.Anonymous.Anonymous.Anonymous.pwszVal.ToString();
                return string.IsNullOrWhiteSpace(name) ? "(unnamed device)" : name;
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read an endpoint's friendly name: {ex.Message}");
            return "(unknown device)";
        }
    }
}
