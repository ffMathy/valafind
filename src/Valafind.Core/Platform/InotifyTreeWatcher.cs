using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Valafind.Core.Platform;

/// <summary>What happened to a path reported by <see cref="InotifyTreeWatcher"/>.</summary>
internal enum TreeEventKind
{
    /// <summary>File or directory created or moved in (a directory's existing contents are not reported).</summary>
    Created,
    /// <summary>File content or metadata changed.</summary>
    Changed,
    /// <summary>File or directory deleted or moved away.</summary>
    Deleted,
}

/// <summary>Problems <see cref="InotifyTreeWatcher"/> reports besides events.</summary>
internal enum TreeWatchProblem
{
    /// <summary>The kernel queue overflowed: events were lost somewhere in the tree.</summary>
    Overflow,
    /// <summary>The per-user watch limit (fs.inotify.max_user_watches) was reached: some directories are not
    /// watched. Reported once per watcher.</summary>
    WatchLimitReached,
}

/// <summary>
/// Recursive directory watcher built directly on Linux inotify. Unlike <see cref="FileSystemWatcher"/> with
/// <c>IncludeSubdirectories</c> (one inotify watch on every subdirectory, plus an inotify instance per watcher), it
/// uses a single inotify instance and places watches only on directories the indexer actually walks: excluded trees
/// such as node_modules, .git or ~/.cache cost no watches and generate no event traffic. Newly created / moved-in
/// directories are watched as they appear. When the per-user watch limit is reached it keeps the watches it has,
/// reports <see cref="TreeWatchProblem.WatchLimitReached"/> once, and stops adding more (periodic full scans cover
/// the rest).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class InotifyTreeWatcher : IDisposable
{
    private const uint InModify = 0x2, InAttrib = 0x4, InCloseWrite = 0x8, InMovedFrom = 0x40, InMovedTo = 0x80,
        InCreate = 0x100, InDelete = 0x200, InDeleteSelf = 0x400, InMoveSelf = 0x800, InUnmount = 0x2000,
        InQOverflow = 0x4000, InIgnored = 0x8000, InOnlyDir = 0x01000000, InDontFollow = 0x02000000,
        InExclUnlink = 0x04000000, InIsDir = 0x40000000;

    private const uint WatchMask = InModify | InAttrib | InCloseWrite | InMovedFrom | InMovedTo | InCreate | InDelete |
                                   InDeleteSelf | InMoveSelf | InOnlyDir | InDontFollow | InExclUnlink;

    private const int InNonBlock = 0x800, InCloexec = 0x80000;
    private const int Eintr = 4, Eagain = 11, Enospc = 28;
    private const short PollIn = 1;
    private const int PollTimeoutMs = 250;
    private const int HeaderSize = 16; // int wd; uint mask; uint cookie; uint len

    private readonly string _root;
    private readonly Func<string, string, bool> _isExcludedDirectory;
    private readonly Action<string, TreeEventKind> _onEvent;
    private readonly Action<TreeWatchProblem> _onProblem;
    private readonly Dictionary<int, string> _pathByWatch = [];
    private readonly Dictionary<string, int> _watchByPath = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _fd;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _limitReached;
    private int _disposed;
    private int _fdClosed;
    private readonly Lock _lifecycle = new();

    /// <param name="root">Directory to watch recursively.</param>
    /// <param name="isExcludedDirectory">(directory, parent) → true when the directory must not be watched.</param>
    /// <param name="onEvent">Called on the watcher thread for every change.</param>
    /// <param name="onProblem">Called on the watcher thread for overflows and the watch limit.</param>
    /// <exception cref="IOException">inotify is unavailable (e.g. too many instances).</exception>
    public InotifyTreeWatcher(string root, Func<string, string, bool> isExcludedDirectory,
        Action<string, TreeEventKind> onEvent, Action<TreeWatchProblem> onProblem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _isExcludedDirectory = isExcludedDirectory;
        _onEvent = onEvent;
        _onProblem = onProblem;
        try
        {
            _fd = inotify_init1(InNonBlock | InCloexec);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("inotify is not available", e);
        }
        if (_fd < 0)
            throw new IOException("inotify_init1 failed", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    /// <summary>Completes once the initial watches are in place.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Number of watched directories (diagnostics and tests).</summary>
    public int WatchCount
    {
        get { lock (_pathByWatch) return _pathByWatch.Count; }
    }

    /// <summary>How long <see cref="Dispose"/> waits for the watcher thread (tests shorten it).</summary>
    internal TimeSpan DisposeJoinTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The inotify descriptor (diagnostics and tests).</summary>
    internal int Descriptor => _fd;

    /// <summary>True once the descriptor was closed (diagnostics and tests).</summary>
    internal bool DescriptorClosed => Volatile.Read(ref _fdClosed) != 0;

    /// <summary>True once the per-user watch limit was hit.</summary>
    public bool LimitReached => Volatile.Read(ref _limitReached);

    /// <summary>Starts the watcher thread: adds the initial watches (walking the tree) and then reads events.</summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_thread is not null)
                return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Valafind inotify" };
            _thread.Start();
        }
    }

    /// <summary>
    /// Watcher thread. It is the only user of <see cref="_fd"/> once started and closes it on exit, so the descriptor
    /// number can never be reused by another component while this thread is still blocked in a syscall on it
    /// (for example enumerating a hung network directory when <see cref="Dispose"/> gives up waiting).
    /// </summary>
    private void Run()
    {
        try
        {
            using var priority = BackgroundPriority.EnterForCurrentThread();
            try
            {
                AddTree(_root, reportCreated: false);
            }
            finally
            {
                _ready.TrySetResult();
            }
            ReadLoop();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A callback (exclusion rules, problem sink) threw something unexpected: stop watching this root rather
            // than crash the process from a background thread. The periodic full scan still covers the tree.
            System.Diagnostics.Debug.WriteLine($"inotify watcher for {_root} stopped: {e}");
        }
        finally
        {
            CloseFd();
        }
    }

    private void CloseFd()
    {
        if (Interlocked.Exchange(ref _fdClosed, 1) == 0)
            _ = close(_fd);
    }

    private unsafe void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        while (!_stopping)
        {
            var pfd = new PollFd { Fd = _fd, Events = PollIn };
            var ready = poll(&pfd, 1, PollTimeoutMs);
            if (_stopping)
                return;
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == Eintr)
                    continue;
                return;
            }
            if (ready == 0 || (pfd.Revents & PollIn) == 0)
                continue;

            nint read;
            fixed (byte* p = buffer)
                read = ReadFd(_fd, p, buffer.Length);
            if (read < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is Eintr or Eagain)
                    continue;
                return;
            }
            Dispatch(buffer.AsSpan(0, (int)read));
        }
    }

    private void Dispatch(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + HeaderSize <= data.Length)
        {
            var wd = BitConverter.ToInt32(data.Slice(offset, 4));
            var mask = BitConverter.ToUInt32(data.Slice(offset + 4, 4));
            var length = (int)BitConverter.ToUInt32(data.Slice(offset + 12, 4));
            var nameBytes = data.Slice(offset + HeaderSize, Math.Min(length, data.Length - offset - HeaderSize));
            offset += HeaderSize + length;
            var nul = nameBytes.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes);
            try
            {
                Handle(wd, mask, name);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A misbehaving callback must not stop the watcher.
            }
        }
    }

    private void Handle(int wd, uint mask, string name)
    {
        if ((mask & InQOverflow) != 0)
        {
            _onProblem(TreeWatchProblem.Overflow);
            return;
        }

        string? directory;
        lock (_pathByWatch)
            _pathByWatch.TryGetValue(wd, out directory);

        if ((mask & InIgnored) != 0)
        {
            Forget(wd);
            return;
        }
        if (directory is null)
            return;

        if (name.Length == 0)
        {
            // Event about the watched directory itself. Children's deletes / moves are reported by the parent; the
            // root has no watched parent, so report it here.
            if ((mask & (InDeleteSelf | InMoveSelf | InUnmount)) != 0 && string.Equals(directory, _root, StringComparison.Ordinal))
                _onEvent(_root, TreeEventKind.Deleted);
            return;
        }

        var path = Path.Join(directory, name);
        var isDirectory = (mask & InIsDir) != 0;
        if (isDirectory)
        {
            if ((mask & (InCreate | InMovedTo)) != 0)
            {
                if (_isExcludedDirectory(path, directory))
                    return;
                AddTree(path, reportCreated: true);
            }
            else if ((mask & (InDelete | InMovedFrom)) != 0)
            {
                if ((mask & InMovedFrom) != 0)
                    ForgetTree(path); // its watches would keep reporting the old path
                _onEvent(path, TreeEventKind.Deleted);
            }
            return;
        }

        if ((mask & (InCreate | InMovedTo)) != 0)
            _onEvent(path, TreeEventKind.Created);
        else if ((mask & (InDelete | InMovedFrom)) != 0)
            _onEvent(path, TreeEventKind.Deleted);
        else if ((mask & (InModify | InCloseWrite | InAttrib)) != 0)
            _onEvent(path, TreeEventKind.Changed);
    }

    /// <summary>Watches <paramref name="start"/> and every non-excluded directory below it (breadth-first, no links).
    /// The watch on a directory is added before it is listed, so nothing created meanwhile is missed.</summary>
    private void AddTree(string start, bool reportCreated)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0 && !_stopping)
        {
            var directory = queue.Dequeue();
            if (!AddWatch(directory))
            {
                if (LimitReached)
                    break;
                continue;
            }
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory, "*", options))
                {
                    if (!_isExcludedDirectory(sub, directory))
                        queue.Enqueue(sub);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
        // Reported after the watches exist, so a walk triggered by the event sees everything created since.
        if (reportCreated)
            _onEvent(start, TreeEventKind.Created);
    }

    private bool AddWatch(string directory)
    {
        if (Volatile.Read(ref _limitReached))
            return false;
        var wd = inotify_add_watch(_fd, directory, WatchMask);
        if (wd < 0)
        {
            if (Marshal.GetLastPInvokeError() == Enospc)
            {
                Volatile.Write(ref _limitReached, true);
                _onProblem(TreeWatchProblem.WatchLimitReached);
            }
            return false;
        }
        lock (_pathByWatch)
        {
            // Re-adding a watched inode returns the same descriptor: replace its path.
            if (_pathByWatch.TryGetValue(wd, out var previous))
                _watchByPath.Remove(previous);
            _pathByWatch[wd] = directory;
            _watchByPath[directory] = wd;
        }
        return true;
    }

    private void Forget(int wd)
    {
        lock (_pathByWatch)
        {
            if (_pathByWatch.Remove(wd, out var path) && _watchByPath.TryGetValue(path, out var current) && current == wd)
                _watchByPath.Remove(path);
        }
    }

    /// <summary>Removes the watches on <paramref name="directory"/> and everything below it.</summary>
    private void ForgetTree(string directory)
    {
        var prefix = directory + Path.DirectorySeparatorChar;
        List<int> victims = [];
        lock (_pathByWatch)
        {
            foreach (var (path, wd) in _watchByPath)
            {
                if (string.Equals(path, directory, StringComparison.Ordinal) || path.StartsWith(prefix, StringComparison.Ordinal))
                    victims.Add(wd);
            }
            foreach (var wd in victims)
            {
                if (_pathByWatch.Remove(wd, out var path))
                    _watchByPath.Remove(path);
            }
        }
        foreach (var wd in victims)
            _ = inotify_rm_watch(_fd, wd);
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stopping = true;
            thread = _thread;
        }
        if (thread is null)
            CloseFd(); // never started: nobody else can be using the descriptor
        else if (thread != Thread.CurrentThread)
            thread.Join(DisposeJoinTimeout); // the thread closes the fd itself when it exits, even after a timeout
        _ready.TrySetResult();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int inotify_init1(int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int inotify_add_watch(int fd, string path, uint mask);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int inotify_rm_watch(int fd, int wd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial nint ReadFd(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
