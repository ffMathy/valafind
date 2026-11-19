using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>A cloud whose online-only files are listed in <see cref="OnlineOnly"/>; downloading one removes it from
/// the set and disposing the lease puts it back, like OneDrive's "Free up space".</summary>
internal sealed class FakeCloud : ICloudFiles
{
    public readonly HashSet<string> OnlineOnly = new(StringComparer.Ordinal);
    public readonly List<string> Downloads = [];
    public readonly List<string> GivenBack = [];
    public bool DownloadFails { get; set; }
    public bool Allowed { get; set; } = true;
    public bool OnlineOnlyWhileRead { get; private set; }

    public bool IsOnlineOnly(string path, FileAttributes attributes)
    {
        lock (OnlineOnly)
            return OnlineOnly.Contains(path);
    }

    public bool CanDownload(string path, long size, bool isMedia) => Allowed;

    /// <summary>When set, one-at-a-time downloads wait for it (a slow connection).</summary>
    public ManualResetEventSlim? DownloadGate { get; set; }

    /// <summary>One-at-a-time downloads started (they may still be waiting at <see cref="DownloadGate"/>).</summary>
    public int DownloadsStarted;

    public IDisposable? Download(string path, long size, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref DownloadsStarted);
        DownloadGate?.Wait(cancellationToken);
        lock (OnlineOnly)
        {
            Downloads.Add(path);
            if (DownloadFails)
                return null;
            OnlineOnly.Remove(path);
        }
        return new Lease(this, path);
    }

    // ------------------------------------------------------------------ requests ahead

    /// <summary>Files requested with <see cref="RequestDownload"/> and not yet released.</summary>
    public readonly HashSet<string> Requested = new(StringComparer.Ordinal);
    public readonly List<string> RequestLog = [];
    public readonly List<string> Released = [];
    public int PeakRequested;
    public bool RefuseRequests { get; set; }

    /// <summary>Requested files arrive right away (the sync client is fast); otherwise they arrive on <see cref="ArriveAll"/>.</summary>
    public volatile bool ArriveImmediatelyField;

    public bool ArriveImmediately
    {
        get => ArriveImmediatelyField;
        set => ArriveImmediatelyField = value;
    }

    /// <summary>Releases report the file as in use (to be retried after a restart).</summary>
    public bool ReleaseBusy { get; set; }

    /// <summary>Journal rows that existed when each request changed its file (record-before-change check).</summary>
    public Func<string, bool>? RecordedBeforeChange { get; set; }
    public bool ChangedBeforeRecorded;

    public CloudMark? RequestDownload(string path, Func<CloudMark, bool> record)
    {
        if (path == FailRequestFor)
            throw new InvalidOperationException("unexpected platform error");
        if (!record(new CloudMark(0)))
            return null;
        if (RecordedBeforeChange is { } recorded && !recorded(path))
            ChangedBeforeRecorded = true;
        lock (OnlineOnly)
        {
            if (RefuseRequests)
                return null;
            Requested.Add(path);
            RequestLog.Add(path);
            PeakRequested = Math.Max(PeakRequested, Requested.Count);
            if (ArriveImmediately)
                OnlineOnly.Remove(path);
        }
        return new CloudMark(0);
    }

    public void ArriveAll()
    {
        lock (OnlineOnly)
        {
            foreach (var path in Requested)
                OnlineOnly.Remove(path);
        }
    }

    public readonly List<bool> FinalReleases = [];

    /// <summary>Releases fail even as a final attempt (the pin could not be restored).</summary>
    public bool ReleaseFails { get; set; }

    /// <summary>Each release takes this long (a sync client or antivirus holding freshly downloaded files).</summary>
    public TimeSpan ReleaseDelay { get; set; }

    /// <summary>Requesting this file throws (an unexpected platform error).</summary>
    public string? FailRequestFor { get; set; }

    public bool Release(string path, CloudMark mark, DateTimeOffset requestedUtc, bool final = false)
    {
        if (ReleaseDelay > TimeSpan.Zero)
            Thread.Sleep(ReleaseDelay);
        lock (OnlineOnly)
        {
            FinalReleases.Add(final);
            if (ReleaseFails || (ReleaseBusy && !final))
                return false;
            Released.Add(path);
            Requested.Remove(path);
            if (File.Exists(path))
                OnlineOnly.Add(path); // online-only again
        }
        return true;
    }

    public int RequestedCount
    {
        get { lock (OnlineOnly) return Requested.Count; }
    }

    public void NoteRead(string path)
    {
        if (IsOnlineOnly(path, 0))
            OnlineOnlyWhileRead = true;
    }

    private sealed class Lease(FakeCloud cloud, string path) : IDisposable
    {
        public void Dispose()
        {
            lock (cloud.OnlineOnly)
            {
                cloud.GivenBack.Add(path);
                cloud.OnlineOnly.Add(path);
            }
        }
    }
}

/// <summary>Reads like <see cref="FakeReader"/> and records whether a file was read while still online-only.</summary>
internal sealed class CloudAwareReader(FakeCloud cloud) : IDocumentReader
{
    private readonly FakeReader _inner = new();
    public readonly List<string> Reads = [];

    /// <summary>Reading a file this returns true for takes <see cref="SlowReadDelay"/> (large files).</summary>
    public Func<string, bool>? SlowRead { get; set; }
    public TimeSpan SlowReadDelay { get; set; }

    public async Task<ExtractedText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        lock (Reads)
            Reads.Add(path);
        cloud.NoteRead(path);
        if (SlowRead is { } slow && slow(path))
            Thread.Sleep(SlowReadDelay); // blocks the worker, like a real extraction
        return await _inner.ReadAsync(path, cancellationToken);
    }
}

public class CloudFileIndexingTests
{
    private static CoordinatorTuning Tuning(ICloudFiles cloud) => CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
        Pace = new IndexingPace(maxConcurrency: 1, processorCount: 1),
        Cloud = cloud,
    };

    private static IndexingCoordinator Create(TempTree tree, FakeStore store, FakeModel model, FakeCloud cloud, IDocumentReader reader)
    {
        var coordinator = new IndexingCoordinator(new FakeDiscovery([tree.Root], [], []), reader, new FakeChunker(), model, store,
            new FakeMonitor(), new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, Tuning(cloud));
        coordinator.OverrideThrottle(TimeSpan.FromMinutes(5));
        return coordinator;
    }

    private static bool Process(IndexingCoordinator coordinator, string path) =>
        coordinator.ProcessFile(path, IndexPhase.All, TestContext.Current.CancellationToken);

    [Fact]
    public async Task OnlineOnlyFileIsDownloadedIndexedByContentAndMadeOnlineOnlyAgain()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/lease.txt", "the landlord keeps the deposit");
        var cloud = new FakeCloud();
        cloud.OnlineOnly.Add(path);
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        var model = new FakeModel();
        await using var coordinator = Create(tree, store, model, cloud, reader);

        Assert.True(Process(coordinator, path));

        Assert.Equal([path], cloud.Downloads);
        Assert.Equal([path], cloud.GivenBack);
        Assert.Contains(path, cloud.OnlineOnly); // online-only again
        Assert.False(cloud.OnlineOnlyWhileRead);
        Assert.Equal([path], reader.Reads);
        var (entry, chunks) = store.Files[path];
        Assert.False(entry.ContentPending);
        Assert.Contains(chunks, c => c.Text.Contains("landlord", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnchangedOnlineOnlyFileIsNotDownloadedAgain()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/notes.txt", "meeting about the budget");
        var cloud = new FakeCloud();
        cloud.OnlineOnly.Add(path);
        var store = new FakeStore();
        await using var coordinator = Create(tree, store, new FakeModel(), cloud, new CloudAwareReader(cloud));

        Assert.True(Process(coordinator, path));
        Assert.True(Process(coordinator, path));

        Assert.Single(cloud.Downloads);
    }

    [Fact]
    public async Task FileThatCannotBeDownloadedIsIndexedByNameWithoutReadingIt()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/Taxes/return_2025.pdf", "%PDF-1.7 not really");
        var cloud = new FakeCloud { DownloadFails = true };
        cloud.OnlineOnly.Add(path);
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        var model = new FakeModel();
        await using var coordinator = Create(tree, store, model, cloud, reader);

        Process(coordinator, path);

        Assert.Empty(reader.Reads);
        var (entry, chunks) = store.Files[path];
        Assert.True(entry.ContentPending);
        Assert.Equal(FileKind.Pdf, entry.Kind);
        Assert.Single(chunks);
        Assert.Contains(model.Calls.SelectMany(c => c), d => d.Text.Contains("return_2025.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NameOnlyFileIsReadOnceItCanBeDownloaded()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/lease.txt", "the landlord keeps the deposit");
        var cloud = new FakeCloud { DownloadFails = true };
        cloud.OnlineOnly.Add(path);
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        await using var coordinator = Create(tree, store, new FakeModel(), cloud, reader);
        Process(coordinator, path);
        Assert.True(store.Files[path].Entry.ContentPending);

        cloud.DownloadFails = false;
        Assert.True(Process(coordinator, path));

        Assert.False(store.Files[path].Entry.ContentPending);
        Assert.Equal([path], reader.Reads);
        Assert.Contains(path, cloud.OnlineOnly);
    }

    [Fact]
    public async Task NameOnlyFileIsLeftAloneWhileDownloadsAreNotAllowed()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/movie.txt", "x");
        var cloud = new FakeCloud { Allowed = false };
        cloud.OnlineOnly.Add(path);
        var model = new FakeModel();
        var store = new FakeStore();
        await using var coordinator = Create(tree, store, model, cloud, new CloudAwareReader(cloud));

        Process(coordinator, path);
        var calls = model.Calls.Count;
        Assert.True(Process(coordinator, path)); // up to date: nothing to retry

        Assert.Empty(cloud.Downloads);
        Assert.Equal(calls, model.Calls.Count);
        Assert.True(store.Files[path].Entry.ContentPending);
    }

    [Fact]
    public async Task NameOnlyFileIsReadOnceTheUserDownloadsIt()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/lease.txt", "the landlord keeps the deposit");
        var cloud = new FakeCloud { Allowed = false };
        cloud.OnlineOnly.Add(path);
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        await using var coordinator = Create(tree, store, new FakeModel(), cloud, reader);
        Process(coordinator, path);

        cloud.OnlineOnly.Remove(path); // the user opened it, or chose "Always keep on this device"
        Assert.True(Process(coordinator, path));

        Assert.False(store.Files[path].Entry.ContentPending);
        Assert.Equal([path], reader.Reads);
        Assert.Empty(cloud.Downloads);
    }

    [Fact]
    public async Task FullyIndexedFileThatGoesOnlineOnlyKeepsItsContent()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/lease.txt", "the landlord keeps the deposit");
        var cloud = new FakeCloud();
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        var model = new FakeModel();
        await using var coordinator = Create(tree, store, model, cloud, reader);
        Process(coordinator, path);

        cloud.OnlineOnly.Add(path); // "Free up space"
        var calls = model.Calls.Count;
        Assert.True(Process(coordinator, path));

        Assert.Empty(cloud.Downloads);
        Assert.Equal(calls, model.Calls.Count);
        Assert.False(store.Files[path].Entry.ContentPending);
    }

    [Fact]
    public async Task OnlineOnlyFileOfUnknownTypeIsNotIndexedByName()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/blob.bin", "x");
        var cloud = new FakeCloud { DownloadFails = true };
        cloud.OnlineOnly.Add(path);
        var store = new FakeStore();
        await using var coordinator = Create(tree, store, new FakeModel(), cloud, new CloudAwareReader(cloud));

        Assert.False(Process(coordinator, path));
        Assert.Empty(store.Files);
    }

    [Fact]
    public void SizeLimitsKeepHugeFilesOnline()
    {
        Assert.True(CloudFiles.IsWithinSizeLimit(10 * 1024 * 1024, isMedia: false));
        Assert.False(CloudFiles.IsWithinSizeLimit(CloudFiles.MaxDocumentBytes + 1, isMedia: false));
        Assert.True(CloudFiles.IsWithinSizeLimit(CloudFiles.MaxDocumentBytes + 1, isMedia: true));
        Assert.False(CloudFiles.IsWithinSizeLimit(CloudFiles.MaxMediaBytes + 1, isMedia: true));
        Assert.False(CloudFiles.IsWithinSizeLimit(0, isMedia: false));
    }

    [Fact]
    public void RegularFilesAreNotOnlineOnly()
    {
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");
        Assert.False(CloudFiles.Current.IsOnlineOnly(path, File.GetAttributes(path)));
    }

    [Fact]
    public void WindowsCloudAttributesMeanOnlineOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Cloud Files attributes are Windows-only.");
            return;
        }
        var cloud = new WindowsCloudFiles();
        Assert.True(cloud.IsOnlineOnly("x", FileAttributes.Archive | FileAttributes.ReparsePoint | CloudFiles.RecallOnDataAccess));
        Assert.True(cloud.IsOnlineOnly("x", FileAttributes.Offline));
        Assert.False(cloud.IsOnlineOnly("x", FileAttributes.Archive | FileAttributes.ReparsePoint | CloudFiles.Pinned));
        // The same bit as FILE_ATTRIBUTE_EA (extended attributes) on a file: an ordinary local file can have it.
        Assert.False(cloud.IsOnlineOnly("x", FileAttributes.Archive | CloudFiles.RecallOnOpen));
    }

    [Fact]
    public void WindowsDownloadOfALocalFileLeavesItAlone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Cloud Files API is Windows-only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local");
        // Not a placeholder: hydration fails (or is a no-op) and the file is untouched.
        using (new WindowsCloudFiles().Download(path, 5, TestContext.Current.CancellationToken))
        {
        }
        Assert.Equal("local", File.ReadAllText(path));
    }

    [Fact]
    public void WindowsNeverFreesUpOrRepinsAFileThatIsNotACloudPlaceholder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Cloud Files API is Windows-only.");
            return;
        }
        using var tree = new TempTree();
        var path = tree.File("plain.txt", "local and precious");
        var attributes = File.GetAttributes(path);
        var cloud = new WindowsCloudFiles();

        // A stale record pointing at a path that now holds an ordinary file: nothing is touched.
        Assert.True(cloud.Release(path, new CloudMark(0, FileId: 12345), DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.NotEqual(WindowsCloudFiles.FreeResult.Freed, WindowsCloudFiles.FreeUpSpace(path, DateTimeOffset.UtcNow));
        Assert.Null(cloud.RequestDownload(path, _ => throw new InvalidOperationException("must not record a local file")));

        Assert.Equal("local and precious", File.ReadAllText(path));
        Assert.Equal(attributes, File.GetAttributes(path));
    }
}
