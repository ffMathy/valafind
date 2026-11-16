using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Valafind.Core.Configuration;

namespace Valafind.App.Platform;

/// <summary>
/// Ensures one Valafind process per user and lets later launches forward a command ("toggle", "show", "quit")
/// to the running instance. The acquire decision uses a per-user named mutex on Windows and an exclusive lock on
/// a file in <see cref="AppPaths.DataDirectory"/> elsewhere; commands travel over a per-user named pipe
/// (a Unix domain socket on macOS/Linux) restricted to the current user.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxCommandBytes = 64;
    private const int MaxServerInstances = 2; // one being served + one listening
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] AllowedCommands = ["toggle", "show", "quit"];

    private readonly IDisposable _instanceLock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _eventGate = new();
    private readonly Queue<string> _pending = new();
    private EventHandler<string>? _commandReceived;
    private Task _serverLoop = Task.CompletedTask;
    private int _disposed;

    private SingleInstance(IDisposable instanceLock, string pipeName)
    {
        _instanceLock = instanceLock;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Raised on a thread-pool thread for each valid command sent by <see cref="SignalExistingAsync"/>.
    /// Commands that arrive before the first handler is attached are queued and delivered to it.
    /// </summary>
    public event EventHandler<string>? CommandReceived
    {
        add
        {
            string[] backlog;
            lock (_eventGate)
            {
                _commandReceived += value;
                backlog = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var command in backlog)
                Raise(command);
        }
        remove
        {
            lock (_eventGate)
                _commandReceived -= value;
        }
    }

    /// <summary>
    /// Claims the per-user instance. Returns null when another Valafind process of the same user already holds it
    /// (forward your command with <see cref="SignalExistingAsync"/> and exit). Keep the returned object alive for
    /// the lifetime of the process and dispose it on shutdown.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var userKey = UserKey();
        IDisposable? instanceLock = OperatingSystem.IsWindows() ? AcquireMutex(userKey) : AcquireLockFile();
        if (instanceLock is null)
            return null;

        var instance = new SingleInstance(instanceLock, PipeEndpoint(userKey));
        instance._serverLoop = Task.Run(instance.ServeAsync);
        return instance;
    }

    /// <summary>
    /// Sends <paramref name="command"/> ("toggle", "show" or "quit") to the running instance. Returns false when the
    /// command is not allowed or no instance answered within 2 seconds.
    /// </summary>
    public static async Task<bool> SignalExistingAsync(string command)
    {
        var normalized = Normalize(command);
        if (normalized is null)
            return false;

        try
        {
            await using var client = new NamedPipeClientStream(
                ".", PipeEndpoint(UserKey()), PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(normalized + "\n");
            await client.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stops listening and releases the instance lock.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop.Cancel();
        try
        {
            _serverLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _instanceLock.Dispose();
        _stop.Dispose();
    }

    internal static string? Normalize(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var trimmed = command.Trim().ToLowerInvariant();
        return Array.IndexOf(AllowedCommands, trimmed) >= 0 ? trimmed : null;
    }

    internal static string PipeName(string userKey) => "valafind-" + userKey;

    /// <summary>
    /// The pipe name handed to System.IO.Pipes. On Linux/BSD, when <c>$XDG_RUNTIME_DIR</c> (a 0700 per-user
    /// directory) is available, an absolute socket path inside it is used so other users cannot squat the name in
    /// the shared /tmp. Elsewhere the plain name is used: Windows pipes and macOS' per-user $TMPDIR are already
    /// private. <see cref="PipeOptions.CurrentUserOnly"/> additionally verifies the peer's identity everywhere.
    /// </summary>
    internal static string PipeEndpoint(string userKey)
    {
        var name = PipeName(userKey);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(runtimeDir) && Path.IsPathRooted(runtimeDir) && Directory.Exists(runtimeDir))
            {
                var path = Path.Combine(runtimeDir, name + ".sock");
                if (Encoding.UTF8.GetByteCount(path) < 100) // sockaddr_un.sun_path is 108 bytes
                    return path;
            }
        }

        return name;
    }

    /// <summary>Stable, filesystem-safe hash of the user identity (user name plus, on Unix, the home directory).</summary>
    internal static string UserKey()
    {
        var identity = Environment.UserDomainName + "\\" + Environment.UserName;
        if (!OperatingSystem.IsWindows())
            identity += "|" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static Mutex? AcquireMutex(string userKey)
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\Valafind-" + userKey, out var createdNew);
        if (createdNew)
            return mutex; // Holding the handle keeps the named object alive; it vanishes with our process.

        mutex.Dispose();
        return null;
    }

    private static IDisposable? AcquireLockFile()
    {
        var directory = AppPaths.DataDirectory;
        var path = Path.Combine(directory, "instance.lock");
        try
        {
            Directory.CreateDirectory(directory);

            // On Unix, FileShare.None makes .NET take an exclusive advisory flock(); it is released automatically if
            // the process dies, so a stale file never blocks a new launch. The file itself is never deleted, which
            // avoids unlink/recreate races between two starting processes.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return stream;
        }
        catch (IOException)
        {
            return null; // Locked by the running instance.
        }
        catch (UnauthorizedAccessException ex)
        {
            // The data directory is unusable; rather than refusing to start, run without the lock. The pipe server
            // still lets a second launch reach us, and creating it fails if another instance already listens.
            System.Diagnostics.Trace.TraceWarning($"Valafind: cannot take the instance lock at {path}: {ex.Message}");
            return new NoLock();
        }
    }

    /// <summary>
    /// Accepts clients one at a time. The next listening instance is created before the connected one is processed
    /// and disposed, so the endpoint never disappears between clients (on Unix the socket would otherwise be closed
    /// and re-bound, losing connections queued in its backlog).
    /// </summary>
    private async Task ServeAsync()
    {
        var token = _stop.Token;
        NamedPipeServerStream? listening = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                listening ??= await CreateServerAsync(token).ConfigureAwait(false);
                if (listening is null)
                    return; // Cancelled while retrying.

                try
                {
                    await listening.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    // Broken instance; replace it.
                    await listening.DisposeAsync().ConfigureAwait(false);
                    listening = null;
                    continue;
                }

                var connected = listening;
                listening = await CreateServerAsync(token).ConfigureAwait(false);
                await using (connected.ConfigureAwait(false))
                {
                    try
                    {
                        var command = await ReadCommandAsync(connected, token).ConfigureAwait(false);
                        if (command is not null)
                            Raise(command);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        // Client vanished mid-message or was too slow; drop it.
                    }
                }
            }
        }
        finally
        {
            if (listening is not null)
                await listening.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a listening pipe instance, retrying with back-off; null once cancelled.</summary>
    private async Task<NamedPipeServerStream?> CreateServerAsync(CancellationToken token)
    {
        for (var failures = 1; !token.IsCancellationRequested; failures++)
        {
            try
            {
                return new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, MaxServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Name briefly busy (previous instance still shutting down) or the socket directory is not writable.
                System.Diagnostics.Trace.TraceWarning($"Valafind: cannot listen for commands: {ex.Message}");
                if (!await DelayAsync(TimeSpan.FromMilliseconds(Math.Min(10_000, 250 * failures)), token).ConfigureAwait(false))
                    break;
            }
        }

        return null;
    }

    private static async Task<string?> ReadCommandAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);

        var buffer = new byte[MaxCommandBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
            {
                length = newline;
                break;
            }
        }

        if (length > MaxCommandBytes)
            return null;

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        return Normalize(text.TrimEnd('\r'));
    }

    private void Raise(string command)
    {
        EventHandler<string>? handler;
        lock (_eventGate)
        {
            handler = _commandReceived;
            if (handler is null)
            {
                if (_pending.Count < 16)
                    _pending.Enqueue(command);
                return;
            }
        }

        try
        {
            handler(this, command);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Valafind: CommandReceived handler threw: {ex}");
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class NoLock : IDisposable
    {
        public void Dispose() { }
    }
}
