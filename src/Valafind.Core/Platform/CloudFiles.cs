using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Valafind.Core.Platform;

/// <summary>
/// Online-only files of cloud sync clients (OneDrive Files On-Demand, iCloud Drive, Dropbox, Google Drive, …): files
/// whose name and size are on disk but whose content is downloaded when something reads it.
/// </summary>
internal interface ICloudFiles
{
    /// <summary>True when the content of the file at <paramref name="path"/> is not on this device.</summary>
    /// <param name="attributes">The file's attributes from the stat the caller already made.</param>
    bool IsOnlineOnly(string path, FileAttributes attributes);

    /// <summary>True when an online-only file of <paramref name="size"/> bytes would be downloaded for indexing.</summary>
    bool CanDownload(string path, long size, bool isMedia);

    /// <summary>Downloads the content of an online-only file so it can be read. Disposing the result frees the
    /// space again (the file goes back to online-only) unless the user has pinned it to this device. Null when the
    /// file could not be downloaded; the file is then left as it was.</summary>
    IDisposable? Download(string path, long size, CancellationToken cancellationToken);
}

/// <summary>The cloud-file support of the current OS.</summary>
internal static class CloudFiles
{
    // Windows file attributes not in the FileAttributes enum.
    internal const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    internal const FileAttributes Pinned = (FileAttributes)0x00080000;
    internal const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Attributes that mark a Windows file whose content is not on this device.</summary>
    internal const FileAttributes OnlineOnlyAttributes = FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen;

    /// <summary>Online-only documents larger than this are not downloaded: their text would not be read anyway
    /// (<see cref="Abstractions.ExtractionLimits.MaxFileBytes"/>).</summary>
    internal const long MaxDocumentBytes = 64L * 1024 * 1024;

    /// <summary>Online-only pictures, audio and video larger than this are not downloaded.</summary>
    internal const long MaxMediaBytes = 1024L * 1024 * 1024;

    /// <summary>Free space that must remain on the drive after a download.</summary>
    internal const long FreeSpaceReserve = 2L * 1024 * 1024 * 1024;

    public static ICloudFiles Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsCloudFiles()
        : OperatingSystem.IsMacOS() ? new MacCloudFiles()
        : new NoCloudFiles();

    internal static bool IsWithinSizeLimit(long size, bool isMedia) =>
        size > 0 && size <= (isMedia ? MaxMediaBytes : MaxDocumentBytes);

    internal static bool HasRoomFor(string path, long size)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return false;
            return new DriveInfo(root).AvailableFreeSpace - size >= FreeSpaceReserve;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>No cloud placeholders (Linux: sync clients there keep full copies).</summary>
internal sealed class NoCloudFiles : ICloudFiles
{
    public bool IsOnlineOnly(string path, FileAttributes attributes) => false;

    public bool CanDownload(string path, long size, bool isMedia) => false;

    public IDisposable? Download(string path, long size, CancellationToken cancellationToken) => null;
}

/// <summary>
/// Windows Cloud Files placeholders (OneDrive Files On-Demand and other sync clients built on the Cloud Files API).
/// <see cref="Download"/> hydrates the file with <c>CfHydratePlaceholder</c>; disposing the result dehydrates it again
/// with <c>CfDehydratePlaceholder</c>, the same thing "Free up space" in Explorer does. When the file cannot be
/// dehydrated right away (another program has it open) it is marked unpinned instead, which tells the sync client to
/// free its space as soon as it can.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCloudFiles : ICloudFiles
{
    private const string CldApi = "cldapi.dll";
    private const uint CfOpenFileFlagExclusive = 0x1;
    private const uint CfOpenFileFlagWriteAccess = 0x2;
    private const int CfPinStateUnpinned = 2;
    private const int DehydrateAttempts = 4;
    private static readonly TimeSpan DehydrateRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Allowed time for a download: a minute plus one second per 256 KB (a slow 2 Mbit/s line).</summary>
    internal static TimeSpan DownloadTimeout(long size) => TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(size / (256.0 * 1024));

    private static volatile bool s_unavailable;

    public bool IsOnlineOnly(string path, FileAttributes attributes) => (attributes & CloudFiles.OnlineOnlyAttributes) != 0;

    public bool CanDownload(string path, long size, bool isMedia) =>
        !s_unavailable && CloudFiles.IsWithinSizeLimit(size, isMedia) && CloudFiles.HasRoomFor(path, size);

    public IDisposable? Download(string path, long size, CancellationToken cancellationToken)
    {
        if (s_unavailable)
            return null;
        FileAttributes before;
        try
        {
            before = File.GetAttributes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        if ((before & CloudFiles.RecallOnDataAccess) == 0)
            return null; // not a Cloud Files placeholder (a local file, or tiered storage): nothing we can hydrate

        try
        {
            using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DownloadTimeout(size));
                int hr;
                using (timeout.Token.Register(static h => CancelIoEx((SafeFileHandle)h!, 0), handle))
                    hr = CfHydratePlaceholder(handle, 0, -1, 0, 0);
                cancellationToken.ThrowIfCancellationRequested();
                if (hr < 0)
                    return null;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        // A file the user pinned to this device ("Always keep on this device") stays downloaded.
        return (before & CloudFiles.Pinned) != 0 ? EmptyLease.Instance : new Lease(path);
    }

    /// <summary>Makes <paramref name="path"/> online-only again.</summary>
    internal static void FreeUpSpace(string path)
    {
        try
        {
            for (var attempt = 1; attempt <= DehydrateAttempts; attempt++)
            {
                if (TryDehydrate(path))
                    return;
                if (attempt < DehydrateAttempts)
                    Thread.Sleep(DehydrateRetryDelay); // antivirus scans freshly downloaded files
            }
            SetUnpinned(path);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
        }
    }

    private static bool TryDehydrate(string path)
    {
        // The Cloud Files API requires an exclusive handle for dehydration so no reader sees the data disappear.
        if (CfOpenFileWithOplock(path, CfOpenFileFlagExclusive | CfOpenFileFlagWriteAccess, out var protectedHandle) < 0)
            return false;
        try
        {
            var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
            return handle != 0 && CfDehydratePlaceholder(handle, 0, -1, 0, 0) >= 0;
        }
        finally
        {
            CfCloseHandle(protectedHandle);
        }
    }

    private static void SetUnpinned(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            _ = CfSetPinState(handle, CfPinStateUnpinned, 0, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        {
        }
    }

    private sealed class Lease(string path) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                FreeUpSpace(path);
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        public static readonly EmptyLease Instance = new();

        public void Dispose()
        {
        }
    }

    [LibraryImport(CldApi)]
    private static partial int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint hydrateFlags, nint overlapped);

    [LibraryImport(CldApi)]
    private static partial int CfDehydratePlaceholder(nint fileHandle, long startingOffset, long length, uint dehydrateFlags, nint overlapped);

    [LibraryImport(CldApi, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfOpenFileWithOplock(string filePath, uint flags, out nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial nint CfGetWin32HandleFromProtectedHandle(nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial void CfCloseHandle(nint fileHandle);

    [LibraryImport(CldApi)]
    private static partial int CfSetPinState(SafeFileHandle fileHandle, int pinState, uint pinFlags, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
