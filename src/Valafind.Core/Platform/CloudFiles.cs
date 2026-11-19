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
    /// space again (the file goes back to online-only) when that is provably safe (see <see cref="Release"/>);
    /// otherwise the file stays downloaded. Null when the file could not be downloaded; it is then left as it was.</summary>
    IDisposable? Download(string path, long size, CancellationToken cancellationToken);

    /// <summary>Asks the sync client to download an online-only file in the background (on Windows: "Always keep on
    /// this device"). Returns what <see cref="Release"/> needs to put the file back as it was, or null when the request
    /// could not be made (the caller then downloads it with <see cref="Download"/> when it gets to it).</summary>
    /// <param name="record">Called with that same information before anything about the file is changed, so it can be
    /// stored durably first (a crash right after the change must not lose it). Returning false cancels the request.</param>
    CloudMark? RequestDownload(string path, Func<CloudMark, bool> record);

    /// <summary>Undoes <see cref="RequestDownload"/>: restores the file's original setting and, when it was downloaded,
    /// makes it online-only again. Safe to call more than once.</summary>
    /// <remarks>Never risks the user's data: the space is freed only when the sync client reports the file as fully
    /// uploaded, no one has written to it since <paramref name="requestedUtc"/>, and neither the user nor a pinned
    /// folder wants it kept on this device. Otherwise the file is left downloaded. A setting the user changed after
    /// the request is left as the user set it.</remarks>
    /// <param name="final">No later retry will come (Valafind is being uninstalled): a file that is in use is not kept
    /// for one. Its original pin is restored and, when it is safe, the sync client is asked to free it once it can.</param>
    /// <returns>False when the file is in use and should be released again later (after a restart); true when
    /// there is nothing more to do.</returns>
    bool Release(string path, CloudMark mark, DateTimeOffset requestedUtc, bool final = false);
}

/// <summary>What <see cref="ICloudFiles.RequestDownload"/> changed, kept (in the index database) until
/// <see cref="ICloudFiles.Release"/> undoes it, across restarts.</summary>
/// <param name="State">Platform-specific: on Windows the file's original pin state, or <see cref="Untouched"/>.</param>
/// <param name="FileId">On Windows the file's volume-wide id when it was requested, so a different file that later
/// takes its path (deleted and created again) is never touched; 0 when unknown.</param>
internal readonly record struct CloudMark(int State, long FileId = 0)
{
    /// <summary>Nothing was changed (the user had already pinned the file to this device): nothing to undo.</summary>
    public const int Untouched = -1;
}

/// <summary>The cloud-file support of the current OS.</summary>
internal static class CloudFiles
{
    // Windows file attributes not in the FileAttributes enum.
    internal const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    internal const FileAttributes Pinned = (FileAttributes)0x00080000;
    internal const FileAttributes Unpinned = (FileAttributes)0x00100000;
    internal const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Attributes that mark a Windows file whose content is not on this device.</summary>
    /// <remarks>Not <see cref="RecallOnOpen"/>: that bit marks directories in directory listings, and on a file it is
    /// the same bit as FILE_ATTRIBUTE_EA (the file has extended attributes), which ordinary local files can have.</remarks>
    internal const FileAttributes OnlineOnlyAttributes = FileAttributes.Offline | RecallOnDataAccess;

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

    public CloudMark? RequestDownload(string path, Func<CloudMark, bool> record) => null;

    public bool Release(string path, CloudMark mark, DateTimeOffset requestedUtc, bool final = false) => true;
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
    private const int CfPinStateUnspecified = 0;
    private const int CfPinStatePinned = 1;
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
        var startedUtc = DateTimeOffset.UtcNow;

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
        return (before & CloudFiles.Pinned) != 0 ? EmptyLease.Instance : new Lease(path, startedUtc);
    }

    public CloudMark? RequestDownload(string path, Func<CloudMark, bool> record)
    {
        if (s_unavailable)
            return null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & CloudFiles.RecallOnDataAccess) == 0)
                return null; // not a placeholder whose content is missing
            if (!TryGetState(path, out var state))
                return null; // not a Cloud Files placeholder we can read: leave it to Download
            if (state.PinState == CfPinStatePinned)
            {
                // The user keeps it on this device; it is coming anyway, and nothing is changed or undone.
                var untouched = new CloudMark(CloudMark.Untouched);
                return record(untouched) ? untouched : null;
            }
            if (state.PinState is not (CfPinStateUnspecified or CfPinStateUnpinned or CfPinStateInherit))
                return null; // excluded from sync, or a state we do not know how to restore
            var mark = new CloudMark(state.PinState, state.FileId);
            if (!record(mark))
                return null;
            // Pinning makes the sync client download the file in the background, many at a time.
            return SetPinState(path, CfPinStatePinned) ? mark : null;
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
    }

    public bool Release(string path, CloudMark mark, DateTimeOffset requestedUtc, bool final = false)
    {
        if (mark.State == CloudMark.Untouched || s_unavailable)
            return true;
        try
        {
            if (!File.Exists(path) || !TryGetState(path, out var state))
                return true; // gone, or no longer a placeholder: nothing of ours is left on it
            if (mark.FileId != 0 && state.FileId != mark.FileId)
                return true; // another file has taken this path since: not ours to touch
            if (state.PinState != CfPinStatePinned)
                return true; // the pin was changed since the request (by the user): keep their choice

            // Written to since the request (the user edited it): put the pin back but never back to "free up space",
            // and leave the file on this device.
            var modified = WrittenSince(path, requestedUtc);
            var original = modified && mark.State == CfPinStateUnpinned ? CfPinStateUnspecified : mark.State;
            if (!SetPinState(path, original))
                return false; // in use: try again after the next restart
            if (modified || (File.GetAttributes(path) & CloudFiles.RecallOnDataAccess) != 0)
                return true; // edited, or never arrived: nothing to free
            // Uninstalling (final) may wait a few seconds; otherwise one try, and the release thread tries again later.
            if (FreeUpSpace(path, requestedUtc, final ? DehydrateAttempts : 1) != FreeResult.Busy)
                return true;
            if (final)
            {
                // In use, and no retry will come: leave the original pin and let the sync client free it once it can
                // (it frees only uploaded files), as "Free up space" would.
                AskToFreeWhenSafe(path, requestedUtc);
                return true;
            }
            // In use: pin it again so the retry after the next restart still recognises it as ours to free.
            SetPinState(path, CfPinStatePinned);
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool SetPinState(string path, int state)
    {
        foreach (var access in new[] { FileAccess.Read, FileAccess.ReadWrite })
        {
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
                if (CfSetPinState(handle, state, 0, 0) >= 0)
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    internal enum FreeResult
    {
        /// <summary>Online-only again.</summary>
        Freed,
        /// <summary>Left downloaded on purpose: not provably safe to free (changes not uploaded, edited, pinned).</summary>
        Kept,
        /// <summary>Another program has the file open: try again later.</summary>
        Busy,
    }

    /// <summary>Makes <paramref name="path"/> online-only again, but only when that cannot lose anything.</summary>
    /// <param name="notWrittenSince">The file must not have been written to after this time.</param>
    /// <param name="attempts">Tries while another program has the file open, a second apart. One (the default) never
    /// sleeps: the caller retries later instead (Valafind's release thread), so no thread is held up.</param>
    internal static FreeResult FreeUpSpace(string path, DateTimeOffset notWrittenSince, int attempts = 1)
    {
        try
        {
            if (IsInPinnedFolder(path))
                return FreeResult.Kept; // the user keeps this folder on the device
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var result = TryDehydrate(path, notWrittenSince);
                if (result != FreeResult.Busy)
                    return result;
                if (attempt < attempts)
                    Thread.Sleep(DehydrateRetryDelay); // antivirus scans freshly downloaded files
            }
            return FreeResult.Busy;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return FreeResult.Kept;
        }
    }

    private static FreeResult TryDehydrate(string path, DateTimeOffset notWrittenSince)
    {
        // An exclusive handle: no one else can open, read or write the file until it is closed, so what is checked
        // below still holds when the data is dropped.
        if (CfOpenFileWithOplock(path, CfOpenFileFlagExclusive | CfOpenFileFlagWriteAccess, out var protectedHandle) < 0)
            return FreeResult.Busy;
        try
        {
            // The Win32 handle stays valid only while the protected handle is referenced: when another program opens
            // the file, the oplock breaks and the platform closes it. Without the reference its value could by then
            // belong to another file.
            if (CfReferenceProtectedHandle(protectedHandle) == 0)
                return FreeResult.Busy;
            try
            {
                var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
                if (handle == 0 || !TryGetState(handle, out var state))
                    return FreeResult.Kept;
                // Only a file the sync client has fully uploaded (in sync, no modified ranges) loses nothing when its
                // local data goes; a file someone pinned or wrote to since is kept.
                if (!state.InSync || state.ModifiedDataSize != 0 || state.PinState == CfPinStatePinned || WrittenSince(path, notWrittenSince))
                    return FreeResult.Kept;
                return CfDehydratePlaceholder(handle, 0, -1, 0, 0) >= 0 ? FreeResult.Freed : FreeResult.Kept;
            }
            finally
            {
                CfReleaseProtectedHandle(protectedHandle);
            }
        }
        finally
        {
            CfCloseHandle(protectedHandle);
        }
    }

    /// <summary>Marks the file "Free up space" (unpinned), so the sync client frees it once it can (only after it is
    /// uploaded), unless the user has a say in it already, it has changes, or it was written to since.</summary>
    private static void AskToFreeWhenSafe(string path, DateTimeOffset notWrittenSince)
    {
        if (TryGetState(path, out var state) && state.PinState is CfPinStateUnspecified or CfPinStateInherit &&
            state.InSync && state.ModifiedDataSize == 0 && !WrittenSince(path, notWrittenSince) && !IsInPinnedFolder(path))
            SetPinState(path, CfPinStateUnpinned);
    }

    private static bool WrittenSince(string path, DateTimeOffset since)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > since.UtcDateTime;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // unknown: assume it was
        }
    }

    private static bool IsInPinnedFolder(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            return parent is null || (File.GetAttributes(parent) & CloudFiles.Pinned) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true; // unknown: assume it is
        }
    }

    /// <summary>The placeholder's pin state, sync state and id (<c>CfGetPlaceholderInfo</c>, standard info).</summary>
    internal readonly record struct PlaceholderState(int PinState, bool InSync, long ModifiedDataSize, long FileId);

    private static bool TryGetState(string path, out PlaceholderState state)
    {
        state = default;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                return TryGetState(handle.DangerousGetHandle(), out state);
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static unsafe bool TryGetState(nint handle, out PlaceholderState state)
    {
        state = default;
        // CF_PLACEHOLDER_STANDARD_INFO: OnDiskDataSize, ValidatedDataSize, ModifiedDataSize, PropertiesSize (8 bytes
        // each), PinState, InSyncState (4 each), FileId, SyncRootFileId (8 each), FileIdentityLength, FileIdentity[].
        const int bufferSize = 64 + 4096; // the fixed part plus the largest file identity
        var buffer = stackalloc byte[bufferSize];
        var hr = CfGetPlaceholderInfo(handle, CfPlaceholderInfoStandard, buffer, bufferSize, out var returned);
        if ((hr < 0 && hr != ErrorMoreData) || returned < 56)
            return false;
        state = new PlaceholderState(
            PinState: *(int*)(buffer + 32),
            InSync: *(int*)(buffer + 36) == CfInSyncStateInSync,
            ModifiedDataSize: *(long*)(buffer + 16),
            FileId: *(long*)(buffer + 40));
        return true;
    }

    private const int CfPinStateInherit = 4;
    private const int CfPlaceholderInfoStandard = 1;
    private const int CfInSyncStateInSync = 1;
    private const int ErrorMoreData = unchecked((int)0x800700EA); // HRESULT_FROM_WIN32(ERROR_MORE_DATA)

    private sealed class Lease(string path, DateTimeOffset startedUtc) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            // Not freed right away because another program has it open: ask the sync client to free it when it can.
            if (FreeUpSpace(path, startedUtc) == FreeResult.Busy)
                AskToFreeWhenSafe(path, startedUtc);
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
    private static unsafe partial int CfGetPlaceholderInfo(nint fileHandle, int infoClass, void* infoBuffer, uint infoBufferLength, out uint returnedLength);

    [LibraryImport(CldApi)]
    private static partial int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint hydrateFlags, nint overlapped);

    [LibraryImport(CldApi)]
    private static partial int CfDehydratePlaceholder(nint fileHandle, long startingOffset, long length, uint dehydrateFlags, nint overlapped);

    [LibraryImport(CldApi, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfOpenFileWithOplock(string filePath, uint flags, out nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial nint CfGetWin32HandleFromProtectedHandle(nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial byte CfReferenceProtectedHandle(nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial void CfReleaseProtectedHandle(nint protectedHandle);

    [LibraryImport(CldApi)]
    private static partial void CfCloseHandle(nint fileHandle);

    [LibraryImport(CldApi)]
    private static partial int CfSetPinState(SafeFileHandle fileHandle, int pinState, uint pinFlags, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
