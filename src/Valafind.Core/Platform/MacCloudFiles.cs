using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;

namespace Valafind.Core.Platform;

/// <summary>
/// macOS "dataless" files: online-only items of iCloud Drive (including Desktop and Documents when they sync to
/// iCloud) and of File Provider clients under ~/Library/CloudStorage (OneDrive, Dropbox, Google Drive, Box, …).
/// </summary>
/// <remarks>
/// <para><see cref="RequestDownload"/> asks the system to download the file in the background
/// (<c>-[NSFileManager startDownloadingUbiquitousItemAtURL:error:]</c>); <see cref="Download"/> (the fallback, one file
/// at a time) reads the first byte, which makes the system download the whole file. Disposing the
/// result evicts it again ("Remove Download" in Finder): first through
/// <c>-[NSFileManager evictUbiquitousItemAtURL:error:]</c>, then through <c>fileproviderctl evict</c>. Eviction is
/// confirmed by checking that the file is dataless again.</para>
/// <para>A provider that refuses eviction (some do, for some items) is not downloaded from again while Valafind runs:
/// its online-only files are indexed by name, so at most one file per provider stays downloaded.</para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacCloudFiles : ICloudFiles
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string FileProviderCtl = "/usr/bin/fileproviderctl";
    private static readonly TimeSpan EvictTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EvictSettle = TimeSpan.FromSeconds(5);

    private static readonly Lock s_initLock = new();
    private static nint s_msgSend;
    private static bool s_objcUnavailable;

    private readonly string _home;
    private readonly Func<string, bool> _isDataless;
    private readonly ConcurrentDictionary<string, byte> _refusesEviction = new(StringComparer.Ordinal);

    public MacCloudFiles()
        : this(AppPaths.Home, MacFileFlags.IsDataless)
    {
    }

    internal MacCloudFiles(string home, Func<string, bool> isDataless)
    {
        _home = Path.GetFullPath(home);
        _isDataless = isDataless;
    }

    /// <summary>Allowed time for a download: a minute plus one second per 256 KB (a slow 2 Mbit/s line).</summary>
    internal static TimeSpan DownloadTimeout(long size) => TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(size / (256.0 * 1024));

    public bool IsOnlineOnly(string path, FileAttributes attributes) => _isDataless(path);

    public bool CanDownload(string path, long size, bool isMedia) =>
        CloudFiles.IsWithinSizeLimit(size, isMedia) && !_refusesEviction.ContainsKey(ProviderKey(path)) &&
        CloudFiles.HasRoomFor(path, size);

    public IDisposable? Download(string path, long size, CancellationToken cancellationToken)
    {
        if (!_isDataless(path))
            return NoLease.Instance; // already on this device: nothing to give back

        var read = Task.Run(() => Materialize(path), CancellationToken.None);
        bool finished;
        try
        {
            finished = read.Wait(DownloadTimeout(size), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The download goes on in the background; give the file back once it is done.
            read.ContinueWith(_ => Evict(path), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            throw;
        }
        if (!finished)
        {
            read.ContinueWith(_ => Evict(path), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return null;
        }
        if (!read.Result || _isDataless(path))
            return null;
        return new Lease(this, path);
    }

    public CloudMark? RequestDownload(string path)
    {
        if (!_isDataless(path) || RefusesEviction(path))
            return null;
        return StartDownloading(path) ? new CloudMark(0) : null;
    }

    public void Release(string path, CloudMark mark)
    {
        if (File.Exists(path) && !_isDataless(path))
            Evict(path);
    }

    /// <summary><c>[[NSFileManager defaultManager] startDownloadingUbiquitousItemAtURL:url error:&amp;error]</c>: the
    /// system downloads the file in the background.</summary>
    internal static bool StartDownloading(string path)
    {
        if (!EnsureObjc())
            return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var manager = Msg(Class("NSFileManager"), "defaultManager");
            var url = FileUrl(path);
            if (manager == 0 || url == 0)
                return false;
            nint error = 0;
            return ((delegate* unmanaged<nint, nint, nint, nint*, byte>)s_msgSend)(
                manager, Selector("startDownloadingUbiquitousItemAtURL:error:"), url, &error) != 0;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Makes <paramref name="path"/> online-only again. True when it is dataless afterwards.</summary>
    internal bool Evict(string path)
    {
        if (_isDataless(path))
            return true;
        if (EvictWithFileManager(path) && WaitUntilDataless(path))
            return true;
        if (EvictWithFileProviderCtl(path) && WaitUntilDataless(path))
            return true;
        _refusesEviction.TryAdd(ProviderKey(path), 0);
        return false;
    }

    /// <summary>Files of one provider share an eviction policy: ~/Library/CloudStorage/&lt;provider&gt; for File
    /// Provider clients, everything else (iCloud Drive, Desktop and Documents in iCloud) counts as iCloud.</summary>
    internal string ProviderKey(string path)
    {
        var cloudStorage = Path.Combine(_home, "Library", "CloudStorage") + Path.DirectorySeparatorChar;
        if (path.StartsWith(cloudStorage, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path.AsSpan(cloudStorage.Length);
            var slash = rest.IndexOf(Path.DirectorySeparatorChar);
            return cloudStorage + (slash < 0 ? rest : rest[..slash]).ToString();
        }
        return "iCloud";
    }

    internal bool RefusesEviction(string path) => _refusesEviction.ContainsKey(ProviderKey(path));

    private static bool Materialize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            stream.ReadByte();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool WaitUntilDataless(string path)
    {
        // Eviction can finish shortly after the call returns.
        var deadline = Environment.TickCount64 + (long)EvictSettle.TotalMilliseconds;
        while (true)
        {
            if (_isDataless(path))
                return true;
            if (Environment.TickCount64 >= deadline || !File.Exists(path))
                return false;
            Thread.Sleep(100);
        }
    }

    /// <summary><c>[[NSFileManager defaultManager] evictUbiquitousItemAtURL:url error:&amp;error]</c>.</summary>
    internal static bool EvictWithFileManager(string path)
    {
        if (!EnsureObjc())
            return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var manager = Msg(Class("NSFileManager"), "defaultManager");
            var url = FileUrl(path);
            if (manager == 0 || url == 0)
                return false;
            nint error = 0;
            return ((delegate* unmanaged<nint, nint, nint, nint*, byte>)s_msgSend)(
                manager, Selector("evictUbiquitousItemAtURL:error:"), url, &error) != 0;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>True when the Objective-C runtime and the NSFileManager download and eviction methods are there.</summary>
    internal static bool FileManagerEvictionAvailable()
    {
        if (!EnsureObjc())
            return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var manager = Msg(Class("NSFileManager"), "defaultManager");
            return manager != 0 && RespondsTo(manager, "evictUbiquitousItemAtURL:error:") &&
                   RespondsTo(manager, "startDownloadingUbiquitousItemAtURL:error:");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static bool RespondsTo(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(receiver, Selector("respondsToSelector:"), Selector(selector)) != 0;

    private static bool EvictWithFileProviderCtl(string path)
    {
        if (!File.Exists(FileProviderCtl))
            return false;
        // The exit code is not trusted: the caller checks that the file is dataless afterwards.
        UserFileDiscovery.RunLines(FileProviderCtl, ["evict", path], EvictTimeout, CancellationToken.None);
        return true;
    }

    private static bool EnsureObjc()
    {
        if (s_msgSend != 0)
            return true;
        lock (s_initLock)
        {
            if (s_msgSend != 0)
                return true;
            if (s_objcUnavailable)
                return false;
            try
            {
                var objc = NativeLibrary.Load(Objc);
                NativeLibrary.Load(Foundation);
                s_msgSend = NativeLibrary.GetExport(objc, "objc_msgSend");
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                s_objcUnavailable = true;
                return false;
            }
        }
    }

    private static nint FileUrl(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\0");
        fixed (byte* p = bytes)
        {
            var text = Msg(Class("NSString"), "stringWithUTF8String:", (nint)p);
            return text == 0 ? 0 : Msg(Class("NSURL"), "fileURLWithPath:", text);
        }
    }

    private static nint Class(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return objc_getClass(p);
    }

    private static nint Selector(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return sel_registerName(p);
    }

    private static nint Msg(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, Selector(selector));

    private static nint Msg(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Selector(selector), a);

    private sealed class Lease(MacCloudFiles owner, string path) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Evict(path);
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }

    [LibraryImport(Objc)]
    private static partial nint objc_getClass(byte* name);

    [LibraryImport(Objc)]
    private static partial nint sel_registerName(byte* name);

    [LibraryImport(Objc)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Objc)]
    private static partial void objc_autoreleasePoolPop(nint pool);
}
