using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>Online-only files are requested from the sync client ahead of the indexer, within the limits, and every
/// request is undone, also across a restart.</summary>
public class CloudPrefetchTests
{
    private static CoordinatorTuning Tuning(FakeCloud cloud, CloudPrefetchOptions? prefetch = null) => CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
        Pace = new IndexingPace(maxConcurrency: 2, processorCount: 2),
        Cloud = cloud,
        Prefetch = prefetch ?? new CloudPrefetchOptions { PollInterval = TimeSpan.FromMilliseconds(20), FreeBytes = _ => 1L << 40 },
    };

    private static IndexingCoordinator Create(TempTree tree, IReadOnlyList<string> files, FakeStore store, FakeCloud cloud,
        IDocumentReader reader, CloudPrefetchOptions? prefetch = null, IFileDiscovery? discovery = null)
    {
        var coordinator = new IndexingCoordinator(discovery ?? new FakeDiscovery([tree.Root], [], files), reader, new FakeChunker(), new FakeModel(),
            store, new FakeMonitor(), new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, Tuning(cloud, prefetch));
        coordinator.OverrideThrottle(TimeSpan.FromMinutes(5));
        return coordinator;
    }

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static List<string> OnlineOnlyFiles(TempTree tree, FakeCloud cloud, int count, int bytes = 40)
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var path = tree.File($"OneDrive/note{i:D3}.txt", $"note {i} " + new string('x', Math.Max(0, bytes - 10)));
            cloud.OnlineOnly.Add(path);
            files.Add(path);
        }
        return files;
    }

    private static bool Settled(IndexingCoordinator coordinator) => coordinator.Status.State == IndexingState.UpToDate;

    /// <summary>Holds every download back until <paramref name="limit"/> files are requested, checks that no more are
    /// requested while none has arrived, then lets the rest through and waits for the pass to finish.</summary>
    private static async Task ReachLimitThenFinish(IndexingCoordinator coordinator, FakeCloud cloud, FakeStore store, int limit, int files)
    {
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => cloud.RequestedCount == limit, $"{limit} files requested");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(limit, cloud.RequestedCount); // the limit holds while nothing arrives

        cloud.ArriveImmediately = true;
        cloud.ArriveAll();
        await WaitFor(() => Settled(coordinator) && store.Files.Count == files && cloud.RequestedCount == 0, "indexed and given back");
        Assert.Equal(limit, cloud.PeakRequested); // and never more after that
    }

    [Fact]
    public async Task FilesAreRequestedAheadThenIndexedAndGivenBack()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 20);
        var reader = new CloudAwareReader(cloud);
        var store = new FakeStore();
        await using var coordinator = Create(tree, files, store, cloud, reader);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        // Nothing has arrived yet, so nothing can be read: every file is requested at once.
        await WaitFor(() => cloud.RequestedCount == files.Count, "all files requested");
        Assert.Empty(reader.Reads);

        cloud.ArriveAll();
        await WaitFor(() => Settled(coordinator) && store.Files.Count == files.Count && cloud.RequestedCount == 0, "indexed and given back");

        Assert.Empty(cloud.Downloads); // never read one at a time
        Assert.False(cloud.OnlineOnlyWhileRead);
        Assert.All(files, f => Assert.Contains(f, cloud.OnlineOnly)); // all online-only again
        Assert.All(files, f => Assert.False(store.Files[f].Entry.ContentPending));
        Assert.Equal(0, store.DownloadCount); // nothing left to undo
    }

    [Fact]
    public async Task AtMostMaxFilesAreRequestedAtOnce()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 12);
        var store = new FakeStore();
        var options = new CloudPrefetchOptions { MaxFiles = 3, PollInterval = TimeSpan.FromMilliseconds(10), FreeBytes = _ => 1L << 40 };
        await using var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud), options);

        await ReachLimitThenFinish(coordinator, cloud, store, limit: 3, files.Count);

        Assert.Equal(files.Count, cloud.RequestLog.Count);
        Assert.Equal(0, store.DownloadCount);
    }

    [Fact]
    public async Task RequestedBytesStayWithinTenPercentOfFreeSpace()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 10, bytes: 40);
        var store = new FakeStore();
        // 10 % of 1000 free bytes: two 40-byte files fit (80 ≤ 0.1 × 1080), a third does not (120 > 0.1 × 1080).
        var options = new CloudPrefetchOptions { PollInterval = TimeSpan.FromMilliseconds(10), FreeBytes = _ => 1000 };
        await using var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud), options);

        await ReachLimitThenFinish(coordinator, cloud, store, limit: 2, files.Count);
    }

    [Fact]
    public async Task FileLargerThanTheBudgetIsReadDirectly()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 1, bytes: 400);
        var store = new FakeStore();
        var options = new CloudPrefetchOptions { PollInterval = TimeSpan.FromMilliseconds(10), FreeBytes = _ => 1000 };
        await using var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud), options);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Settled(coordinator) && store.Files.Count == 1, "indexed");

        Assert.Empty(cloud.RequestLog);
        Assert.Equal(files, cloud.Downloads);
        Assert.Contains(files[0], cloud.OnlineOnly);
    }

    [Fact]
    public async Task RequestsThatNeverArriveAreGivenBackAndReadDirectly()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 4);
        var store = new FakeStore();
        var options = new CloudPrefetchOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            StallTimeout = TimeSpan.FromMilliseconds(200),
            FreeBytes = _ => 1L << 40,
        };
        await using var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud), options);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Settled(coordinator) && store.Files.Count == files.Count, "indexed");

        Assert.Equal(files.Order(StringComparer.Ordinal), cloud.Downloads.Order(StringComparer.Ordinal)); // read directly
        Assert.Equal(0, cloud.RequestedCount); // every request undone
        Assert.All(files, f => Assert.Contains(f, cloud.OnlineOnly));
        Assert.Equal(0, store.DownloadCount);
    }

    [Fact]
    public async Task RefusedRequestsFallBackToReadingDirectly()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { RefuseRequests = true };
        var files = OnlineOnlyFiles(tree, cloud, 3);
        var store = new FakeStore();
        await using var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Settled(coordinator) && store.Files.Count == files.Count, "indexed");

        Assert.Equal(files.Count, cloud.Downloads.Count);
        Assert.Equal(0, store.DownloadCount);
    }

    [Fact]
    public async Task RequestsFromBeforeARestartAreIndexedIfTheyArrivedAndAlwaysGivenBack()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var arrived = tree.File("OneDrive/arrived.txt", "the landlord keeps the deposit");
        var stillOnline = tree.File("OneDrive/still-online.txt", "not here yet");
        var deleted = tree.P("OneDrive/deleted.txt");
        cloud.OnlineOnly.Add(stillOnline); // `arrived` downloaded while Valafind was not running
        var store = new FakeStore();
        var at = DateTimeOffset.UtcNow;
        foreach (var path in new[] { arrived, stillOnline, deleted })
        {
            store.AddDownload(new CloudDownload(path, 30, IndexPhase.All, new CloudMark(0), at));
            cloud.Requested.Add(path);
        }
        var reader = new CloudAwareReader(cloud);
        await using var coordinator = Create(tree, [], store, cloud, reader);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Settled(coordinator) && store.DownloadCount == 0, "earlier requests handled");

        Assert.Equal([arrived], reader.Reads); // the one that arrived is indexed by content…
        Assert.False(store.Files[arrived].Entry.ContentPending);
        Assert.False(store.Files.ContainsKey(stillOnline)); // …the others are not read
        Assert.Contains(arrived, cloud.Released); // and every request is undone
        Assert.Contains(stillOnline, cloud.Released);
        Assert.Contains(arrived, cloud.OnlineOnly);
        Assert.Contains(stillOnline, cloud.OnlineOnly);
    }

    [Fact]
    public async Task RequestsStayRecordedWhileValafindIsStopped()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var files = OnlineOnlyFiles(tree, cloud, 5);
        var store = new FakeStore();
        await using (var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud)))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => cloud.RequestedCount == files.Count, "all files requested");
        }

        // Stopped before anything arrived: every request is still on record for the next start…
        Assert.Equal(files.Count, store.DownloadCount);

        // …which arrives while Valafind is down, and is indexed and given back after the restart.
        cloud.ArriveAll();
        await using (var coordinator = Create(tree, files, store, cloud, new CloudAwareReader(cloud)))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => Settled(coordinator) && store.DownloadCount == 0 && store.Files.Count == files.Count, "indexed and given back");
        }
        Assert.Equal(0, cloud.RequestedCount);
        Assert.All(files, f => Assert.Contains(f, cloud.OnlineOnly));
    }

    [Fact]
    public void BudgetCountsRequestedBytesBackIn()
    {
        var cloud = new FakeCloud();
        var store = new FakeStore();
        using var prefetcher = new CloudPrefetcher(cloud, store, TimeProvider.System,
            new CloudPrefetchOptions { FreeBytes = _ => 900 }, StringComparer.Ordinal);
        var root = Path.GetTempPath();

        // 10 % of (900 free + 0 held) = 90: a 60-byte file fits…
        Assert.Equal(CloudPrefetcher.HoldResult.Held, prefetcher.TryHold(Path.Combine(root, "a"), 60, IndexPhase.All, null));
        // …a second does not (120 > 10 % of 960), so wait for room…
        Assert.Equal(CloudPrefetcher.HoldResult.NoRoom, prefetcher.TryHold(Path.Combine(root, "b"), 60, IndexPhase.All, null));
        // …and one that would never fit is read directly.
        Assert.Equal(CloudPrefetcher.HoldResult.Failed, prefetcher.TryHold(Path.Combine(root, "c"), 500, IndexPhase.All, null));

        prefetcher.Release(Path.Combine(root, "a"));
        Assert.Equal(0, prefetcher.Count);
        Assert.Equal(0L, prefetcher.HeldBytes);
        Assert.Equal(0, store.DownloadCount);
    }

    [Fact]
    public async Task WalkReadsANameOnlyFileOnceItIsOnTheDevice()
    {
        using var tree = new TempTree();
        var path = tree.File("OneDrive/lease.txt", "the landlord keeps the deposit");
        var info = new FileInfo(path);
        var store = new FakeStore();
        // Indexed by name earlier, while it was online-only; same size and time now, but its content is here.
        store.UpsertFile(new FileEntry(path, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            FileKind.Text, IndexPhase.All, ContentPending: true), []);
        var cloud = new FakeCloud();
        var reader = new CloudAwareReader(cloud);
        // A discovery that reports sizes and times, so the walk's batch check for unchanged files is used.
        await using var coordinator = Create(tree, [path], store, cloud, reader, discovery: new FakeStatDiscovery([tree.Root], [path]));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Settled(coordinator) && !store.Files[path].Entry.ContentPending, "content indexed");

        Assert.Equal([path], reader.Reads);
    }
}
