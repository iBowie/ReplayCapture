using ReplayCapture.Core.Diagnostics;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace ReplayCapture.Core.Audio;

/// <summary>
/// Watches for the system default playback/recording device changing.
/// <para>
/// A <see cref="WasapiCaptureSource"/> opened against "the default device" resolves that endpoint
/// once, at open time, via <c>IMMDeviceEnumerator::GetDefaultAudioEndpoint</c> — it does not follow
/// the default afterwards. If the user then switches their default playback or recording device,
/// the already-open capture client keeps listening to the endpoint that is no longer default, which
/// has nothing routed to it anymore, so the track it feeds goes silent with no error. This watcher
/// is what lets <see cref="AudioEngine"/> notice the change and reopen against the new default.
/// </para>
/// </summary>
internal sealed class DefaultDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    /// <summary>
    /// Raised on the OS notification thread when the default endpoint changes for the role this app
    /// actually resolves against: eConsole for <see cref="AudioDeviceEnumerator.CreateDefaultRenderLoopback"/>,
    /// eCommunications for <see cref="AudioDeviceEnumerator.CreateDefaultCapture"/>. Windows calls
    /// <c>OnDefaultDeviceChanged</c> once per role (up to three times per switch); the other roles are
    /// filtered out here so subscribers don't see redundant notifications.
    /// </summary>
    public event Action<EDataFlow>? DefaultDeviceChanged;

    public DefaultDeviceWatcher()
    {
        _enumerator = AudioDeviceEnumerator.CreateEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
    {
        var relevantRole = flow == EDataFlow.eRender ? ERole.eConsole : ERole.eCommunications;
        if (role != relevantRole) return;

        try
        {
            DefaultDeviceChanged?.Invoke(flow);
        }
        catch (Exception ex)
        {
            Log.Error("Handling a default audio device change failed", ex);
        }
    }

    public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) { }

    public void OnDeviceAdded(PCWSTR pwstrDeviceId) { }

    public void OnDeviceRemoved(PCWSTR pwstrDeviceId) { }

    public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not unregister default audio device notifications: {ex.Message}");
        }
    }
}
