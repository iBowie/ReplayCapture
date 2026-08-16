using Tmds.DBus.Protocol;

namespace ReplayCapture.Core.Capture.Linux.Portal;

/// <summary>
/// Negotiates a display-capture PipeWire stream through <c>org.freedesktop.portal.ScreenCast</c> —
/// the only way an unprivileged app is allowed to reach the compositor's screen content on Wayland.
/// <para>
/// <b>Draft — compiles against the real <c>Tmds.DBus.Protocol</c> 0.94.2 API (verified by decoding
/// the installed package's metadata directly, not guessed from memory), but still unverified against
/// a real compositor/portal implementation.</b> The protocol sequence itself (CreateSession →
/// SelectSources → Start → OpenPipeWireRemote, each of the first three replying asynchronously via a
/// <c>Response</c> signal on a per-call <c>Request</c> object) is documented and stable —
/// <see href="https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html"/>.
/// What's untested is everything that only a real portal implementation can confirm: whether the
/// predicted request object path actually matches what the compositor hands back (the spec allows it
/// not to, and that case is deliberately left unhandled below — see <see cref="CallAndAwaitResponseAsync"/>),
/// the exact shape of the "streams" result, and how <c>persist_mode</c>/<c>restore_token</c> behave in
/// practice across GNOME/KDE.
/// </para>
/// <para>
/// One real, unresolved product question this class surfaces rather than answers: <c>SelectSources</c>
/// and <c>Start</c> pop the compositor's own picker/consent UI — there is no way to silently automate
/// "capture this monitor" the way DXGI enumeration does on Windows. <see cref="RestoreToken"/> lets a
/// later run skip that picker (via <c>persist_mode</c>), but the *first* run of an always-on
/// background service still needs a real interactive session to grant that consent once. This needs
/// an answer from whoever designs the Linux App shell (Phase 6), not from Core alone.
/// </para>
/// </summary>
public sealed class ScreenCastPortalSession : IDisposable
{
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private const string PortalObjectPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    // org.freedesktop.portal.ScreenCast source_type bitmask.
    private const uint SourceTypeMonitor = 1;

    // org.freedesktop.portal.ScreenCast cursor_mode bitmask.
    private const uint CursorModeEmbedded = 2;

    // org.freedesktop.portal.ScreenCast persist_mode enum: 0 none, 1 while the app runs, 2 until revoked.
    private const uint PersistModeUntilRevoked = 2;

    private readonly DBusConnection _connection;
    private string? _sessionHandle;
    private int _requestCounter;

    public string? RestoreToken { get; private set; }
    public uint NodeId { get; private set; }
    public int PipeWireFd { get; private set; }

    private ScreenCastPortalSession(DBusConnection connection) => _connection = connection;

    /// <param name="restoreToken">
    /// A token from a previous session's <see cref="RestoreToken"/>, to skip the picker/consent UI
    /// when the compositor still honors it. Null on first run.
    /// </param>
    public static async Task<ScreenCastPortalSession> StartAsync(string? restoreToken = null)
    {
        var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();

        var session = new ScreenCastPortalSession(connection);
        await session.NegotiateAsync(restoreToken);
        return session;
    }

    private async Task NegotiateAsync(string? restoreToken)
    {
        var createResults = await CallAndAwaitResponseAsync(
            "CreateSession",
            signature: "a{sv}",
            writeArgs: writer =>
            {
                var start = writer.WriteDictionaryStart();
                WriteStringOption(writer, "session_handle_token", NewToken());
                writer.WriteDictionaryEnd(start);
            });

        _sessionHandle = createResults["session_handle"].GetString();

        await CallAndAwaitResponseAsync(
            "SelectSources",
            signature: "oa{sv}",
            writeArgs: writer =>
            {
                writer.WriteObjectPath(_sessionHandle!);
                var start = writer.WriteDictionaryStart();
                WriteUInt32Option(writer, "types", SourceTypeMonitor);
                WriteUInt32Option(writer, "cursor_mode", CursorModeEmbedded);
                WriteUInt32Option(writer, "persist_mode", PersistModeUntilRevoked);
                if (restoreToken is not null) WriteStringOption(writer, "restore_token", restoreToken);
                writer.WriteDictionaryEnd(start);
            });

        var startResults = await CallAndAwaitResponseAsync(
            "Start",
            signature: "osa{sv}",
            writeArgs: writer =>
            {
                writer.WriteObjectPath(_sessionHandle!);
                writer.WriteString(""); // parent_window: none — this is a background service, not a window.
                var start = writer.WriteDictionaryStart();
                writer.WriteDictionaryEnd(start);
            });

        if (startResults.TryGetValue("restore_token", out var token)) RestoreToken = token.GetString();

        // "streams" is a(ua{sv}): array of (node_id, properties). Only the first stream matters for a
        // single-monitor session — multi-display needs one portal session per display.
        var streams = startResults["streams"].GetArray<VariantValue>();
        NodeId = streams[0].GetItem(0).GetUInt32();

        PipeWireFd = await OpenPipeWireRemoteAsync();
    }

    private async Task<int> OpenPipeWireRemoteAsync()
    {
        // MessageWriter is a ref struct and cannot be live across an `await` — build the message
        // buffer to completion (and let the writer go out of scope) before awaiting anything.
        MessageBuffer message;
        using (var writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: PortalService,
                path: PortalObjectPath,
                @interface: ScreenCastInterface,
                member: "OpenPipeWireRemote",
                signature: "oa{sv}");

            writer.WriteObjectPath(_sessionHandle!);
            var start = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(start);

            message = writer.CreateMessage();
        }

        // OpenPipeWireRemote replies synchronously with the fd — unlike CreateSession/SelectSources/
        // Start, it needs no user interaction, so it does not go through the Request/Response dance.
        return await _connection.CallMethodAsync(
            message,
            (Message reply, object? _) => reply.GetBodyReader().ReadHandleRaw().ToInt32(),
            null);
    }

    /// <summary>
    /// Calls one of the three Request-pattern methods (CreateSession/SelectSources/Start): sends the
    /// method call, which replies immediately with a <c>Request</c> object path, then awaits that
    /// object's <c>Response</c> signal for the real result. This two-step dance — not a plain
    /// request/reply — is what lets the compositor show a picker/consent dialog before answering.
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> CallAndAwaitResponseAsync(
        string member, Action<MessageWriter> writeArgs, string signature)
    {
        var handleToken = NewToken();
        var uniqueName = _connection.UniqueName
            ?? throw new InvalidOperationException("D-Bus connection has no unique name yet — was ConnectAsync awaited?");
        var expectedRequestPath =
            $"/org/freedesktop/portal/desktop/request/{uniqueName[1..].Replace('.', '_')}/{handleToken}";

        var tcs = new TaskCompletionSource<Dictionary<string, VariantValue>>();

        // WatchSignalAsync's non-obsolete overload takes Action<Notification<T>> instead of
        // (Exception?, T) — not switched to yet since Notification<T>'s exact shape wasn't part of
        // this session's metadata check; functionally equivalent either way.
#pragma warning disable CS0618
        using var subscription = await _connection.WatchSignalAsync(
            PortalService,
            expectedRequestPath,
            RequestInterface,
            "Response",
            (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results);
            },
            (Exception? exception, (uint Code, Dictionary<string, VariantValue> Results) value) =>
            {
                if (exception is not null) { tcs.TrySetException(exception); return; }
                if (value.Code != 0)
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"Portal request '{expectedRequestPath}' failed or was cancelled (code {value.Code})."));
                    return;
                }
                tcs.TrySetResult(value.Results);
            },
            null, true, default);
#pragma warning restore CS0618

        // MessageWriter is a ref struct and cannot be live across an `await` — build the message
        // buffer to completion (and let the writer go out of scope) before awaiting anything.
        MessageBuffer message;
        using (var writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: PortalService,
                path: PortalObjectPath,
                @interface: ScreenCastInterface,
                member: member,
                signature: signature);
            writeArgs(writer);

            message = writer.CreateMessage();
        }

        var actualRequestPath = await _connection.CallMethodAsync(
            message,
            (Message reply, object? _) => reply.GetBodyReader().ReadObjectPathAsString(),
            null);

        if (actualRequestPath != expectedRequestPath)
        {
            // The compositor is allowed to hand back a different request path than the one our
            // handle_token predicts, per the portal spec's own caveat about this. Not handled here —
            // a real implementation needs to resubscribe against actualRequestPath instead of
            // assuming the prediction held.
            throw new NotSupportedException(
                $"Portal returned request path '{actualRequestPath}', not the predicted '{expectedRequestPath}' " +
                "— resubscribing against the actual path is not implemented in this draft.");
        }

        return await tcs.Task;
    }

    private static void WriteStringOption(MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    private static void WriteUInt32Option(MessageWriter writer, string key, uint value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantUInt32(value);
    }

    private string NewToken() => $"replaycapture{Interlocked.Increment(ref _requestCounter)}";

    public void Dispose() => _connection.Dispose();
}
