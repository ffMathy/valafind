using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Valafind.Core.Indexing;

namespace Valafind.Core.Platform;

/// <summary>
/// macOS FSEvents history: every volume with an event store (<c>.fseventsd</c>) keeps a log of changed paths that
/// survives restarts. A checkpoint is the system-wide event id at that moment plus each root's event-store UUID; a
/// replay opens a stream with <c>sinceWhen</c> = that id and reads until FSEvents reports the end of its history.
/// </summary>
/// <remarks>A changed UUID means the volume's event store was reset (or the root moved to another volume), so its ids
/// no longer line up with the checkpoint: the replay declines and the caller walks. Network and some external volumes
/// have no event store; <see cref="Capture"/> returns null for them. Only the members that call into macOS carry the
/// platform attribute, so the event mapping and checkpoint format can be tested anywhere.</remarks>
internal sealed unsafe partial class FsEventsHistory : IChangeHistory
{
    private const string CoreServicesLib = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string FormatVersion = "fsevents-v1";
    private const uint CfStringEncodingUtf8 = 0x08000100;
    private const int StatBufferSize = 256;

    // FSEventStreamCreateFlags
    private const uint CreateFlagNoDefer = 0x02;
    private const uint CreateFlagFileEvents = 0x10;

    // FSEventStreamEventFlags
    private const uint FlagMustScanSubDirs = 0x01;
    private const uint FlagEventIdsWrapped = 0x08;
    private const uint FlagHistoryDone = 0x10;
    private const uint FlagRootChanged = 0x20;
    private const uint FlagMount = 0x40;
    private const uint FlagUnmount = 0x80;
    private const uint FlagItemCreated = 0x100;
    private const uint FlagItemRemoved = 0x200;
    private const uint FlagItemRenamed = 0x800;
    private const uint FlagItemIsDir = 0x20000;
    private const uint ItemFlags = 0x00FFFF00; // ItemCreated … ItemCloned

    /// <summary>The instance the coordinator uses: a replay that has not finished after two minutes gives way to a walk.</summary>
    [SupportedOSPlatform("macos")]
    public static FsEventsHistory Instance { get; } = new(TimeSpan.FromMinutes(2));

    private readonly TimeSpan _timeout;

    [SupportedOSPlatform("macos")]
    internal FsEventsHistory(TimeSpan timeout) => _timeout = timeout;

    /// <inheritdoc />
    public string? Capture(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        try
        {
            // The id first: anything that happens after it (while the UUIDs are read) is replayed next time.
            var id = FSEventsGetCurrentEventId();
            var text = new StringBuilder(FormatVersion).Append('\n').Append(id.ToString(CultureInfo.InvariantCulture));
            foreach (var root in roots.Order(StringComparer.Ordinal))
            {
                if (root.Contains('\n', StringComparison.Ordinal) || root.Contains('\t', StringComparison.Ordinal) ||
                    VolumeUuid(root) is not { } uuid)
                    return null;
                text.Append('\n').Append(uuid).Append('\t').Append(root);
            }
            return text.ToString();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryReplay(string checkpoint, IReadOnlyList<string> roots, Action<string, ChangeKind> onChange, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(onChange);
        try
        {
            if (!TryParse(checkpoint, out var since, out var saved) || !IsCurrent(saved, roots))
                return false;
            return Replay(since, roots, onChange, cancellationToken);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryParse(string checkpoint, out ulong since, out List<(string Uuid, string Root)> roots)
    {
        since = 0;
        roots = [];
        var lines = checkpoint.Split('\n');
        if (lines.Length < 2 || lines[0] != FormatVersion ||
            !ulong.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out since))
            return false;
        foreach (var line in lines.Skip(2))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
                return false;
            roots.Add((line[..tab], line[(tab + 1)..]));
        }
        return true;
    }

    /// <summary>Same roots as when the checkpoint was taken, each still on the same event store.</summary>
    private static bool IsCurrent(List<(string Uuid, string Root)> saved, IReadOnlyList<string> roots)
    {
        var current = roots.Order(StringComparer.Ordinal).ToList();
        if (current.Count != saved.Count)
            return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i], saved[i].Root, StringComparison.Ordinal) ||
                !string.Equals(VolumeUuid(current[i]), saved[i].Uuid, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private bool Replay(ulong since, IReadOnlyList<string> roots, Action<string, ChangeKind> onChange, CancellationToken cancellationToken)
    {
        using var session = new ReplaySession(onChange, roots.Select(r => (RealPath(r) ?? r, r)).ToList());
        var handle = GCHandle.Alloc(session);
        nint paths = 0, stream = 0, queue = 0;
        var started = false;
        try
        {
            paths = CreatePathArray(roots);
            if (paths == 0)
                return false;
            var context = new StreamContext { Info = GCHandle.ToIntPtr(handle) };
            var callback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, uint*, ulong*, void>)&OnEvents;
            stream = FSEventStreamCreate(0, callback, &context, paths, since, 0.0, CreateFlagNoDefer | CreateFlagFileEvents);
            if (stream == 0)
                return false;
            queue = dispatch_queue_create("app.valafind.fsevents-replay", 0); // serial
            if (queue == 0)
                return false;
            FSEventStreamSetDispatchQueue(stream, queue);
            started = FSEventStreamStart(stream) != 0;
            if (!started)
                return false;

            var signalled = WaitHandle.WaitAny([session.Done.WaitHandle, cancellationToken.WaitHandle], _timeout);
            cancellationToken.ThrowIfCancellationRequested();
            return signalled != WaitHandle.WaitTimeout && session.Succeeded;
        }
        finally
        {
            session.Close();
            if (stream != 0)
            {
                if (started)
                    FSEventStreamStop(stream);
                if (queue != 0)
                    FSEventStreamInvalidate(stream);
                FSEventStreamRelease(stream);
            }
            if (queue != 0)
            {
                // Serial queue: once this no-op has run, no callback is still using the session.
                dispatch_sync_f(queue, 0, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Noop);
                dispatch_release(queue);
            }
            if (paths != 0)
                CFRelease(paths);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Noop(nint context)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvents(nint stream, nint info, nuint count, nint paths, uint* flags, ulong* ids)
    {
        try
        {
            if (GCHandle.FromIntPtr(info).Target is ReplaySession session)
                session.Handle((int)count, (nint*)paths, flags);
        }
        catch (Exception)
        {
            // Never let an exception cross into FSEvents.
        }
    }

    /// <summary>Maps one batch of FSEvents to changes. FSEvents reports real paths (<c>/private/var</c> for
    /// <c>/var</c>, the target of a symlinked root); they are mapped back to the roots as configured.</summary>
    private sealed class ReplaySession(Action<string, ChangeKind> onChange, List<(string Real, string Root)> roots) : IDisposable
    {
        private volatile bool _closed;

        public ManualResetEventSlim Done { get; } = new(false);
        public bool Succeeded { get; private set; }

        public void Handle(int count, nint* paths, uint* flags)
        {
            for (var i = 0; i < count && !_closed && !Done.IsSet; i++)
            {
                var f = flags[i];
                if ((f & FlagEventIdsWrapped) != 0)
                {
                    Done.Set();
                    return;
                }
                if ((f & FlagHistoryDone) != 0)
                {
                    Succeeded = true;
                    Done.Set();
                    return;
                }
                if (Marshal.PtrToStringUTF8(paths[i]) is not { Length: > 0 } path)
                    continue;
                if (Classify(f) is { } kind)
                    Report(path, kind);
            }
        }

        private void Report(string path, ChangeKind kind)
        {
            var reported = false;
            foreach (var (real, root) in roots)
            {
                if (IsSameOrUnder(path, real))
                {
                    var rest = path[real.TrimEnd('/').Length..];
                    onChange(rest.Length == 0 ? root : root.TrimEnd('/') + rest, kind);
                    return;
                }
                if (kind == ChangeKind.Rescan && IsSameOrUnder(real, path))
                {
                    onChange(root, ChangeKind.Rescan); // detail lost above this root
                    reported = true;
                }
            }
            if (!reported)
                onChange(path, kind);
        }

        private static bool IsSameOrUnder(string path, string directory)
        {
            var trimmed = directory.TrimEnd('/');
            return path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) &&
                   (path.Length == trimmed.Length || path[trimmed.Length] == '/');
        }

        public void Close() => _closed = true;

        public void Dispose() => Done.Dispose();
    }

    internal static ChangeKind? Classify(uint flags)
    {
        if ((flags & FlagMustScanSubDirs) != 0)
            return ChangeKind.Rescan;
        if ((flags & ItemFlags) == 0)
        {
            // Volume and root bookkeeping carry nothing to index; a bare event means "something in this directory".
            return (flags & (FlagRootChanged | FlagMount | FlagUnmount)) != 0 ? null : ChangeKind.Upsert;
        }
        if ((flags & FlagItemIsDir) != 0)
            return (flags & (FlagItemCreated | FlagItemRemoved | FlagItemRenamed)) != 0 ? ChangeKind.Upsert : null;
        return ChangeKind.Changed;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The event-store UUID of the volume holding <paramref name="path"/>, or null when it has none.</summary>
    private static string? VolumeUuid(string path)
    {
        var buffer = stackalloc byte[StatBufferSize];
        var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? stat_inode64(path, buffer) : stat(path, buffer);
        if (rc != 0)
            return null;
        var device = *(int*)buffer; // st_dev is the first field of struct stat
        var uuid = FSEventsCopyUUIDForDevice(device);
        if (uuid == 0)
            return null;
        try
        {
            var text = CFUUIDCreateString(0, uuid);
            if (text == 0)
                return null;
            try
            {
                return ToManaged(text);
            }
            finally
            {
                CFRelease(text);
            }
        }
        finally
        {
            CFRelease(uuid);
        }
    }

    private static string? RealPath(string path)
    {
        nint resolved;
        try
        {
            resolved = realpath_extsn(path, 0);
        }
        catch (EntryPointNotFoundException)
        {
            resolved = realpath(path, 0);
        }
        if (resolved == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    private static string? ToManaged(nint cfString)
    {
        var buffer = stackalloc byte[128]; // a UUID string is 36 characters
        return CFStringGetCString(cfString, buffer, 128, CfStringEncodingUtf8) != 0
            ? Marshal.PtrToStringUTF8((nint)buffer)
            : null;
    }

    private static nint CreatePathArray(IReadOnlyList<string> roots)
    {
        var strings = new nint[roots.Count];
        try
        {
            for (var i = 0; i < roots.Count; i++)
            {
                strings[i] = CFStringCreateWithCString(0, roots[i], CfStringEncodingUtf8);
                if (strings[i] == 0)
                    return 0;
            }
            var callbacks = NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundationLib), "kCFTypeArrayCallBacks");
            fixed (nint* values = strings)
                return CFArrayCreate(0, values, strings.Length, callbacks); // retains the strings
        }
        finally
        {
            foreach (var s in strings)
            {
                if (s != 0)
                    CFRelease(s);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamContext
    {
        public nint Version;
        public nint Info;
        public nint Retain;
        public nint Release;
        public nint CopyDescription;
    }

    [LibraryImport(CoreServicesLib)]
    private static partial ulong FSEventsGetCurrentEventId();

    [LibraryImport(CoreServicesLib)]
    private static partial nint FSEventsCopyUUIDForDevice(int device);

    [LibraryImport(CoreServicesLib)]
    private static partial nint FSEventStreamCreate(nint allocator, nint callback, StreamContext* context, nint pathsToWatch,
        ulong sinceWhen, double latency, uint flags);

    [LibraryImport(CoreServicesLib)]
    private static partial void FSEventStreamSetDispatchQueue(nint stream, nint queue);

    [LibraryImport(CoreServicesLib)]
    private static partial byte FSEventStreamStart(nint stream);

    [LibraryImport(CoreServicesLib)]
    private static partial void FSEventStreamStop(nint stream);

    [LibraryImport(CoreServicesLib)]
    private static partial void FSEventStreamInvalidate(nint stream);

    [LibraryImport(CoreServicesLib)]
    private static partial void FSEventStreamRelease(nint stream);

    [LibraryImport(CoreFoundationLib)]
    private static partial void CFRelease(nint value);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFUUIDCreateString(nint allocator, nint uuid);

    [LibraryImport(CoreFoundationLib)]
    private static partial byte CFStringGetCString(nint value, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFArrayCreate(nint allocator, nint* values, nint count, nint callbacks);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint dispatch_queue_create(string label, nint attributes);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_release(nint value);

    [LibraryImport(LibSystem)]
    private static partial void dispatch_sync_f(nint queue, nint context, nint work);

    [LibraryImport(LibSystem, EntryPoint = "realpath$DARWIN_EXTSN", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath_extsn(string path, nint resolved);

    [LibraryImport(LibSystem, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport(LibSystem)]
    private static partial void free(nint value);

    [LibraryImport(LibSystem, EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int stat(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "stat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int stat_inode64(string path, byte* buffer);
}
