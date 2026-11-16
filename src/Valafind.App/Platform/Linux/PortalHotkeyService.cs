using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Tmds.DBus.Protocol;

namespace Valafind.App.Platform;

/// <summary>
/// Global hotkey on Wayland through the XDG Desktop Portal <c>org.freedesktop.portal.GlobalShortcuts</c>
/// interface (KDE Plasma 5.27+/6, GNOME 48+, Hyprland, ...). Normal Wayland clients cannot grab keys, so the
/// compositor owns the binding: we ask for a shortcut with id "toggle" and a <c>preferred_trigger</c>, the desktop
/// may show a confirmation dialog or let the user pick a different key, and we react to <c>Activated</c> signals.
/// When the portal (or the interface) is missing, registration fails with guidance to bind
/// <c>&lt;path to Valafind&gt; --toggle</c> manually (see <see cref="GlobalHotkeyFactory.ToggleCommand"/>).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
internal sealed class PortalHotkeyService : IGlobalHotkeyService
{
    internal const string ShortcutId = "toggle";
    internal const string AppId = "app.valafind.Valafind";

    private const string PortalBus = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string ShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string SessionInterface = "org.freedesktop.portal.Session";

    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long TryRegister waits for BindShortcuts before assuming the user is looking at a dialog.</summary>
    private static readonly TimeSpan BindGrace = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _connection;
    private IDisposable? _activatedWatch;
    private string? _sessionHandle;
    private HotkeyChord? _activeChord;
    private CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private volatile Action? _callback;
    private volatile string? _activeChordText;

    public string? ActiveChord => _activeChordText;

    public bool TryRegister(string chord, Action onPressed, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        if (!HotkeyChord.TryParse(chord, out var parsed, out error))
            return false;

        if (PreferredTrigger(parsed) is null)
        {
            error = $"The key '{parsed.Key}' cannot be used as a global shortcut.";
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        // Run on the thread pool so we never block on a captured UI synchronization context.
        var task = Task.Run(() => RegisterAsync(parsed, onPressed));
        try
        {
            if (!task.Wait(SetupTimeout + BindGrace))
            {
                error = "Timed out waiting for the desktop portal. " + GlobalHotkeyFactory.WaylandUnavailableMessage;
                return false;
            }

            error = task.Result;
        }
        catch (AggregateException ex)
        {
            error = $"The desktop portal failed: {ex.InnerException?.Message ?? ex.Message}";
        }

        return error is null;
    }

    public void Unregister()
    {
        if (_disposed)
            return;
        _gate.Wait();
        try
        {
            _callback = null;
            _activeChordText = null;
            _activeChord = null;
            CloseActiveSessionAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _gate.Wait();
        try
        {
            _callback = null;
            _activeChordText = null;
            try
            {
                CloseActiveSessionAsync().Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }

            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        _lifetime.Dispose();
    }

    /// <summary>Trigger in the shortcuts-spec syntax, e.g. "CTRL+ALT+space", "LOGO+space".</summary>
    internal static string? PreferredTrigger(HotkeyChord chord)
    {
        var key = HotkeyKeyMaps.XKeysymName(chord.Key);
        if (key is null)
            return null;
        var sb = new StringBuilder();
        if (chord.Ctrl) sb.Append("CTRL+");
        if (chord.Alt) sb.Append("ALT+");
        if (chord.Shift) sb.Append("SHIFT+");
        if (chord.Super) sb.Append("LOGO+");
        sb.Append(key);
        return sb.ToString();
    }

    private async Task<string?> RegisterAsync(HotkeyChord chord, Action onPressed)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return "The hotkey service has been disposed.";

            if (_activeChord == chord && _sessionHandle is not null)
            {
                _callback = onPressed;
                return null;
            }

            using var setupCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            setupCts.CancelAfter(SetupTimeout);

            DBusConnection connection;
            try
            {
                connection = await EnsureConnectionAsync().ConfigureAwait(false);
                if (!await HasGlobalShortcutsAsync(connection).WaitAsync(setupCts.Token).ConfigureAwait(false))
                    return GlobalHotkeyFactory.WaylandUnavailableMessage;
            }
            catch (Exception ex) when (ex is DBusExceptionBase or OperationCanceledException or IOException)
            {
                ResetConnectionIfBroken(ex);
                return GlobalHotkeyFactory.WaylandUnavailableMessage;
            }

            // A session binds its shortcuts once, so a new chord gets a fresh session. The previous session stays
            // live until the new binding is accepted, so a refused chord leaves the old one working.
            string session;
            try
            {
                var (code, results) = await RequestAsync(connection, "CreateSession", "a{sv}", (ref MessageWriter w, string token) =>
                {
                    var dict = w.WriteDictionaryStart();
                    WriteEntry(ref w, "handle_token", token);
                    WriteEntry(ref w, "session_handle_token", NewToken());
                    w.WriteDictionaryEnd(dict);
                }, setupCts.Token).ConfigureAwait(false);

                if (code != 0 || !results.TryGetValue("session_handle", out var handle))
                    return $"The desktop refused to create a shortcut session (response {code}).";
                session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
            }
            catch (Exception ex) when (ex is DBusExceptionBase or OperationCanceledException)
            {
                ResetConnectionIfBroken(ex);
                return $"Could not create a desktop shortcut session: {ex.Message}";
            }

            IDisposable watch;
            try
            {
                watch = await connection.WatchSignalAsync(
                    null, PortalPath, ShortcutsInterface, "Activated",
                    static (Message m, object? _) =>
                    {
                        var reader = m.GetBodyReader();
                        var sessionHandle = reader.ReadObjectPathAsString();
                        var id = reader.ReadString();
                        return (sessionHandle, id);
                    },
                    (Notification<(string Session, string Id)> n) =>
                    {
                        if (n.HasValue && n.Value.Id == ShortcutId && n.Value.Session == session && _sessionHandle == session)
                            HotkeyDispatch.Raise(_callback);
                    },
                    ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false);
            }
            catch (DBusExceptionBase ex)
            {
                ResetConnectionIfBroken(ex);
                await CloseSessionAsync(session, null).ConfigureAwait(false);
                return $"Could not listen for desktop shortcuts: {ex.Message}";
            }

            var trigger = PreferredTrigger(chord)!;
            var bindTask = RequestAsync(connection, "BindShortcuts", "oa(sa{sv})sa{sv}", (ref MessageWriter w, string token) =>
            {
                w.WriteObjectPath(session);
                var shortcuts = w.WriteArrayStart(DBusType.Struct);
                w.WriteStructureStart();
                w.WriteString(ShortcutId);
                var props = w.WriteDictionaryStart();
                WriteEntry(ref w, "description", "Show or hide Valafind");
                WriteEntry(ref w, "preferred_trigger", trigger);
                w.WriteDictionaryEnd(props);
                w.WriteArrayEnd(shortcuts);
                w.WriteString(string.Empty); // parent_window
                var options = w.WriteDictionaryStart();
                WriteEntry(ref w, "handle_token", token);
                w.WriteDictionaryEnd(options);
            }, _lifetime.Token);

            // Plasma answers at once; GNOME may show a dialog. Report quick failures, otherwise treat it as pending.
            var finished = await Task.WhenAny(bindTask, Task.Delay(BindGrace)).ConfigureAwait(false);
            if (finished == bindTask && DescribeBindResult(bindTask, chord) is { } failure)
            {
                if (bindTask.Exception?.InnerException is { } bindError)
                    ResetConnectionIfBroken(bindError);
                await CloseSessionAsync(session, watch).ConfigureAwait(false);
                return failure;
            }

            var (previousSession, previousWatch) = (_sessionHandle, _activatedWatch);
            _sessionHandle = session;
            _activatedWatch = watch;
            _callback = onPressed;
            _activeChord = chord;
            _activeChordText = chord.ToString();
            await CloseSessionAsync(previousSession, previousWatch).ConfigureAwait(false);

            if (finished != bindTask)
            {
                _ = bindTask.ContinueWith(t =>
                {
                    if (DescribeBindResult(t, chord) is not { } lateFailure || _sessionHandle != session)
                        return;
                    System.Diagnostics.Trace.TraceWarning($"Valafind: {lateFailure}");
                    _activeChordText = null;
                    _activeChord = null;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? DescribeBindResult(Task<(uint Code, Dictionary<string, VariantValue> Results)> task, HotkeyChord chord)
    {
        if (task.IsCanceled)
            return null;
        if (task.Exception is { } ex)
            return $"The desktop rejected the shortcut {chord.DisplayString()}: {ex.InnerException?.Message ?? ex.Message}";
        var (code, _) = task.Result;
        return code switch
        {
            0 => null,
            1 => "The shortcut request was cancelled in the desktop dialog. " + GlobalHotkeyFactory.WaylandUnavailableMessage,
            _ => $"The desktop could not bind {chord.DisplayString()} (response {code}).",
        };
    }

    private async Task<DBusConnection> EnsureConnectionAsync()
    {
        if (_connection is not null)
            return _connection;

        var address = DBusAddress.Session ?? throw new IOException("No D-Bus session bus address.");
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        // Host (unsandboxed) apps must announce their app id before any other portal call (xdg-desktop-portal
        // 1.19+); older portals lack the Registry and identify us through the .desktop file / cgroup instead.
        try
        {
            var message = BuildCall(connection, PortalBus, PortalPath, "org.freedesktop.host.portal.Registry", "Register", "sa{sv}",
                static (ref MessageWriter w) =>
                {
                    w.WriteString(AppId);
                    var dict = w.WriteDictionaryStart();
                    w.WriteDictionaryEnd(dict);
                });
            await connection.CallMethodAsync(message).WaitAsync(SetupTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
        {
            // Optional; GlobalShortcuts still works where the desktop can identify us otherwise.
        }

        _connection = connection;
        return connection;
    }

    private static async Task<bool> HasGlobalShortcutsAsync(DBusConnection connection)
    {
        var message = BuildCall(connection, PortalBus, PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss",
            static (ref MessageWriter w) =>
            {
                w.WriteString(ShortcutsInterface);
                w.WriteString("version");
            });
        try
        {
            var version = await connection.CallMethodAsync(message,
                static (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            return version.Type == VariantValueType.UInt32 ? version.GetUInt32() >= 1 : true;
        }
        catch (DBusErrorReplyException)
        {
            // ServiceUnknown (no portal) or InvalidArgs/UnknownInterface (no GlobalShortcuts backend).
            return false;
        }
    }

    private delegate void BodyWriter(ref MessageWriter writer, string handleToken);

    private delegate void ArgsWriter(ref MessageWriter writer);

    private static MessageBuffer BuildCall(
        DBusConnection connection, string destination, string path, string @interface, string member, string? signature, ArgsWriter writeArgs)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature!, MessageFlags.None);
            writeArgs(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Calls a portal method that answers through an <c>org.freedesktop.portal.Request</c> object, subscribing to
    /// its Response signal before the call so the reply cannot be missed.
    /// </summary>
    private static async Task<(uint Code, Dictionary<string, VariantValue> Results)> RequestAsync(
        DBusConnection connection, string method, string signature, BodyWriter writeBody, CancellationToken cancellationToken)
    {
        var token = NewToken();
        var sender = (connection.UniqueName ?? string.Empty).TrimStart(':').Replace('.', '_');
        var expectedPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

        var tcs = new TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watches = new List<IDisposable>(2);
        try
        {
            watches.Add(await WatchResponseAsync(connection, expectedPath, tcs).ConfigureAwait(false));

            var message = BuildCall(connection, PortalBus, PortalPath, ShortcutsInterface, method, signature,
                (ref MessageWriter w) => writeBody(ref w, token));
            var returnedPath = await connection.CallMethodAsync(message,
                static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);

            if (returnedPath != expectedPath) // Very old portals ignore handle_token.
                watches.Add(await WatchResponseAsync(connection, returnedPath, tcs).ConfigureAwait(false));

            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var watch in watches)
                watch.Dispose();
        }
    }

    private static ValueTask<IDisposable> WatchResponseAsync(
        DBusConnection connection, string path, TaskCompletionSource<(uint, Dictionary<string, VariantValue>)> tcs) =>
        connection.WatchSignalAsync(
            null, path, RequestInterface, "Response",
            static (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                var code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results);
            },
            (Notification<(uint Code, Dictionary<string, VariantValue> Results)> n) =>
            {
                if (n.HasValue)
                    tcs.TrySetResult(n.Value);
                else if (n.Exception is not null)
                    tcs.TrySetException(n.Exception);
            },
            ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed, emitOnCapturedContext: false);

    private Task CloseActiveSessionAsync()
    {
        var (session, watch) = (_sessionHandle, _activatedWatch);
        _sessionHandle = null;
        _activatedWatch = null;
        return CloseSessionAsync(session, watch);
    }

    private async Task CloseSessionAsync(string? session, IDisposable? watch)
    {
        watch?.Dispose();
        var connection = _connection;
        if (session is null || connection is null)
            return;

        try
        {
            var message = BuildCall(connection, PortalBus, session, SessionInterface, "Close", null, static (ref MessageWriter _) => { });
            await connection.CallMethodAsync(message).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or TimeoutException)
        {
            // The session may already be gone (portal restarted); nothing else to release.
        }
    }

    /// <summary>Drops a connection that has failed so the next registration reconnects.</summary>
    private void ResetConnectionIfBroken(Exception ex)
    {
        if (ex is not DBusConnectionException || _connection is null)
            return;
        _connection.Dispose();
        _connection = null;
        _sessionHandle = null;
        _activatedWatch = null;
        _activeChord = null;
        _activeChordText = null;
    }

    private static void WriteEntry(ref MessageWriter writer, string key, string value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(key);
        writer.WriteVariantString(value);
    }

    private static string NewToken() => "valafind_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
