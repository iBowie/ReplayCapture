using ReplayCapture.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell.PropertiesSystem;

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
public static class AudioDeviceEnumerator
{
    /// <summary>
    /// PKEY_Device_FriendlyName. CsWin32 does not project this one, so it is spelled out — it is a
    /// documented, fixed property key.
    /// </summary>
    private static readonly PROPERTYKEY DeviceFriendlyNameKey = new()
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
                enumerator.GetDefaultAudioEndpoint(flow, ERole.eConsole, out var defaultDevice);
                defaultId = GetId(defaultDevice);
            }
            catch (Exception ex)
            {
                Log.Warn($"No default {flow} endpoint: {ex.Message}");
            }

            enumerator.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var collection);
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
        CreateEnumerator().GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);
        return new WasapiCaptureSource(device, loopback: true, name);
    }

    /// <summary>Opens the default recording device — the microphone.</summary>
    public static WasapiCaptureSource CreateDefaultCapture(string name = "Mic")
    {
        // eCommunications, not eConsole: Windows tracks a separate "the one you talk into" default,
        // and that is the one a user means by "my mic".
        CreateEnumerator().GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device);
        return new WasapiCaptureSource(device, loopback: false, name);
    }

    /// <summary>Opens a specific endpoint by id, for config that pins a device rather than following the default.</summary>
    public static WasapiCaptureSource CreateForEndpoint(string endpointId, bool loopback, string name)
    {
        CreateEnumerator().GetDevice(endpointId, out var device);
        return new WasapiCaptureSource(device, loopback, name);
    }

    /// <summary>Internal so <see cref="DefaultDeviceWatcher"/> can register for endpoint notifications on the same enumerator type.</summary>
    internal static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumerator();

    /// <summary>Opens the session manager for the default playback device, for session enumeration.</summary>
    internal static unsafe IAudioSessionManager2 OpenDefaultRenderSessionManager()
    {
        CreateEnumerator().GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var device);

        var iid = typeof(IAudioSessionManager2).GUID;
        device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var manager);
        return (IAudioSessionManager2)manager;
    }

    private static unsafe string GetId(IMMDevice device)
    {
        Windows.Win32.Foundation.PWSTR id = default;
        device.GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(id.Value);
        }
    }

    private static unsafe string GetFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out var store);
            store.GetValue(DeviceFriendlyNameKey, out var value);

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
