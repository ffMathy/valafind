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

/// <summary>
/// How a Windows file Valafind pinned is given back, in two steps that never wait for the sync client:
/// <list type="number">
/// <item>Pinned (as Valafind left it): mark it unpinned ("Free up space"). The sync client frees it itself, and only
/// once it is uploaded.</item>
/// <item>Unpinned: once it is online-only again, put back its original pin setting.</item>
/// </list>
/// A file written to since the request is someone's work in progress: it keeps (or gets back) its original setting,
/// never "free up space", and stays on the device. So does one that never arrived, or whose folder is kept on the
/// device. A pin the user changed meanwhile is left as it is.
/// </summary>
internal static class CloudPinPolicy
{
    internal const int Unspecified = 0;
    internal const int Pinned = 1;
    internal const int Unpinned = 2;
    internal const int Inherit = 4;

    /// <param name="SetPin">The pin state to set, or null.</param>
    /// <param name="Done">True when nothing is left to do once <paramref name="SetPin"/> is set; false when the file
    /// must be looked at again later (the sync client has not freed it yet).</param>
    internal readonly record struct Step(int? SetPin, bool Done);

    /// <param name="pinState">The file's pin state now.</param>
    /// <param name="original">Its pin state before Valafind pinned it.</param>
    /// <param name="onlineOnly">Its content is not on the device (never arrived, or freed).</param>
    /// <param name="modified">Written to since Valafind pinned it.</param>
    /// <param name="inPinnedFolder">Its folder is kept on the device.</param>
    internal static Step Decide(int pinState, int original, bool onlineOnly, bool modified, bool inPinnedFolder)
    {
        var keep = modified && original == Unpinned ? Unspecified : original;
        return pinState switch
        {
            Pinned when modified || onlineOnly || inPinnedFolder => new Step(keep, Done: true),
            Pinned => new Step(Unpinned, Done: original == Unpinned), // step 1
            Unpinned when original != Unpinned && modified => new Step(keep, Done: true),
            Unpinned when original != Unpinned && onlineOnly => new Step(original, Done: true), // step 2
            Unpinned when original != Unpinned => new Step(null, Done: false), // not freed yet
            _ => new Step(null, Done: true), // changed by the user since: theirs
        };
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
/// <see cref="RequestDownload"/> pins a file ("Always keep on this device") so the sync client downloads it, and
/// <see cref="Download"/> hydrates one directly (<c>CfHydratePlaceholder</c>). Files are given back the way
/// "Free up space" in Explorer does it: they are marked unpinned and the sync client frees them itself, only once they
/// are uploaded. Valafind never drops a file's data on its own: dehydrating needs the sync client's cooperation, and
/// waiting for it from Valafind's side could hang (an exclusive handle blocks the client's own access to the file).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCloudFiles : ICloudFiles
{
    private const string CldApi = "cldapi.dll";
    private const int CfPinStateUnspecified = 0;
    private const int CfPinStatePinned = 1;
    private const int CfPinStateUnpinned = 2;

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

            var onlineOnly = (File.GetAttributes(path) & CloudFiles.RecallOnDataAccess) != 0;
            var modified = WrittenSince(path, requestedUtc);
            var step = CloudPinPolicy.Decide(state.PinState, mark.State, onlineOnly, modified,
                state.PinState == CfPinStatePinned && !modified && !onlineOnly && IsInPinnedFolder(path));
            if (step.SetPin is { } pin && !SetPinState(path, pin))
                return final; // in use: try again later
            return step.Done || final;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return final;
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

    /// <summary>Marks the file "Free up space" (unpinned): the sync client frees it itself, and only once it is uploaded.
    /// Not when the user has a say in it already (pinned, or in a pinned folder) or it was written to since.</summary>
    private static void AskToFreeWhenSafe(string path, DateTimeOffset notWrittenSince)
    {
        if (TryGetState(path, out var state) && state.PinState is CfPinStateUnspecified or CfPinStateInherit &&
            !WrittenSince(path, notWrittenSince) && !IsInPinnedFolder(path))
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
    private static partial int CfSetPinState(SafeFileHandle fileHandle, int pinState, uint pinFlags, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle file, nint overlapped);
}
