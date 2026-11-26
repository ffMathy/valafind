using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>Online-only files go through a lane of their own: waiting for the cloud never holds back files that are
/// already on this device.</summary>
public class CloudLaneTests
{
    private static CoordinatorTuning Tuning(FakeCloud cloud, CloudPrefetchOptions? prefetch = null, int workers = 2,
        TimeSpan? releaseTimeout = null) => CoordinatorTuning.Default with
    {
        CloudReleaseTimeout = releaseTimeout ?? TimeSpan.FromMilliseconds(300),
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
        Pace = new IndexingPace(maxConcurrency: workers, processorCount: workers),
        Cloud = cloud,
        Prefetch = prefetch ?? new CloudPrefetchOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            StallTimeout = TimeSpan.FromMinutes(10), // a download that is slow, not stuck
            FreeBytes = _ => 1L << 40,
        },
    };

    private static IndexingCoordinator Create(IReadOnlyList<string> roots, IReadOnlyList<string> files, FakeStore store, FakeCloud cloud,
        IDocumentReader reader, CloudPrefetchOptions? prefetch = null, int workers = 2, TimeSpan? releaseTimeout = null)
    {
        var coordinator = new IndexingCoordinator(new FakeDiscovery(roots, [], files), reader, new FakeChunker(), new FakeModel(),
            store, new FakeMonitor(), new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null,
            Tuning(cloud, prefetch, workers, releaseTimeout));
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

    private static (List<string> Cloud, List<string> Local) Files(TempTree tree, FakeCloud cloud, int cloudCount, int localCount)
    {
        var online = new List<string>();
        for (var i = 0; i < cloudCount; i++)
        {
            var path = tree.File($"OneDrive/report{i}.txt", $"cloud report {i}");
            cloud.OnlineOnly.Add(path);
            online.Add(path);
        }
        var local = new List<string>();
        for (var i = 0; i < localCount; i++)
            local.Add(tree.File($"Documents/note{i}.txt", $"local note {i}"));
        // Pictures come after every document in a pass: these show that the pass moved on past the documents.
        local.Add(tree.File("Pictures/holiday.png", "not really a picture"));
        local.Add(tree.File("Pictures/cat.jpg", "not really a picture"));
        return (online, local);
    }

    [Fact]
    public async Task LocalFilesAreIndexedWhileCloudFilesAreStillDownloading()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var (online, local) = Files(tree, cloud, 5, 6);
        var store = new FakeStore();
        // Cloud files first in the walk: the worst case for a pass that would wait for them.
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => local.All(store.Files.ContainsKey), "every local file indexed, pictures included");

        // Nothing has arrived from the cloud: requested (the cloud lane may still be getting there: it runs on its own),
        // not indexed, and the index is not up to date yet.
        await WaitFor(() => cloud.RequestedCount == online.Count, "every cloud file requested");
        Assert.DoesNotContain(online, store.Files.ContainsKey);
        Assert.NotEqual(IndexingState.UpToDate, coordinator.Status.State);
        Assert.NotEqual("1", store.GetMeta(IndexingCoordinator.MetaPhase2Complete)); // the walk waits for the lane

        cloud.ArriveAll();
        await WaitFor(() => online.All(store.Files.ContainsKey) && coordinator.Status.State == IndexingState.UpToDate,
            "cloud files indexed and up to date");
        await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaPhase2Complete) == "1", "walk recorded as complete");
        // Giving back runs on its own thread and never holds up the walk, so it may finish after the index is up to date.
        await WaitFor(() => cloud.RequestedCount == 0, "every cloud file given back");
    }

    [Fact]
    public async Task FullRequestLimitsDoNotHoldBackLocalFiles()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var (online, local) = Files(tree, cloud, 4, 6);
        var store = new FakeStore();
        var options = new CloudPrefetchOptions
        {
            MaxFiles = 1,
            PollInterval = TimeSpan.FromMilliseconds(20),
            StallTimeout = TimeSpan.FromMinutes(10),
            FreeBytes = _ => 1L << 40,
        };
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud), options);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => local.All(store.Files.ContainsKey), "every local file indexed");
        // The limit is full, and the rest wait in the cloud lane (which runs on its own, so it may still be getting there).
        await WaitFor(() => cloud.RequestedCount == 1, "the one allowed cloud file requested");

        cloud.ArriveImmediately = true;
        cloud.ArriveAll();
        await WaitFor(() => online.All(store.Files.ContainsKey) && cloud.RequestedCount == 0, "cloud files indexed and given back");
        Assert.Equal(1, cloud.PeakRequested);
    }

    [Fact]
    public async Task OneAtATimeDownloadsNeverTakeAWorker()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { RefuseRequests = true }; // no requests ahead: every file is downloaded one at a time
        using var slowConnection = new ManualResetEventSlim(false);
        cloud.DownloadGate = slowConnection;
        var (online, local) = Files(tree, cloud, 2, 4);
        var store = new FakeStore();
        // One worker: a download on it would hold back every local file.
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud), workers: 1);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => Volatile.Read(ref cloud.DownloadsStarted) >= 1, "a download under way");
        await WaitFor(() => local.All(store.Files.ContainsKey), "every local file indexed while it downloads");
        Assert.DoesNotContain(online, store.Files.ContainsKey);

        slowConnection.Set();
        await WaitFor(() => online.All(store.Files.ContainsKey) && coordinator.Status.State == IndexingState.UpToDate, "cloud files indexed");
        await WaitFor(() => online.All(f => cloud.IsOnlineOnly(f, 0)), "given back"); // on the release thread
    }

    [Fact]
    public async Task WatcherChangesAreHandledWhileTheWalkWaitsForTheCloud()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud();
        var (online, local) = Files(tree, cloud, 2, 2);
        var store = new FakeStore();
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => local.All(store.Files.ContainsKey) && cloud.RequestedCount == online.Count, "local files indexed");

        // While the walk waits for the cloud lane, a new local file appears.
        var created = tree.File("Documents/new.txt", "written while the cloud downloads");
        coordinator.OnWatcherEvent(created, ChangeKind.Upsert);
        await WaitFor(() => store.Files.ContainsKey(created), "new file indexed without waiting for the cloud");
        Assert.DoesNotContain(online, store.Files.ContainsKey);

        cloud.ArriveAll();
        await WaitFor(() => online.All(store.Files.ContainsKey), "cloud files indexed");
    }

    [Fact]
    public async Task SlowGivingBackNeverHoldsBackLocalFiles()
    {
        using var tree = new TempTree();
        // Giving each file back takes 2 s (OneDrive or antivirus holding fresh downloads): that must not happen on a
        // worker, or a single one would spend its time waiting.
        var cloud = new FakeCloud { ArriveImmediately = true, ReleaseDelay = TimeSpan.FromSeconds(2) };
        var (online, local) = Files(tree, cloud, 6, 8);
        var store = new FakeStore();
        // Slow, not hung: well within the time a give-back may take.
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud), workers: 1,
            releaseTimeout: TimeSpan.FromSeconds(30));

        var started = Stopwatch.StartNew();
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => local.All(store.Files.ContainsKey), "every local file indexed");

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(6), $"local files took {started.Elapsed}");
        await WaitFor(() => online.All(store.Files.ContainsKey) && cloud.RequestedCount == 0, "cloud files indexed and given back");
    }

    [Fact]
    public async Task BusyCloudIndexingNeverHoldsBackLocalFiles()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { ArriveImmediately = true };
        var (online, local) = Files(tree, cloud, 40, 10);
        var onlineSet = online.ToHashSet(StringComparer.Ordinal);
        // Every cloud file takes a while to index and they all arrive at once: the workers stay busy with them.
        var reader = new CloudAwareReader(cloud) { SlowRead = onlineSet.Contains, SlowReadDelay = TimeSpan.FromMilliseconds(300) };
        var store = new FakeStore();
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, reader, workers: 2);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => local.All(store.Files.ContainsKey), "every local file indexed, pictures included");

        // Local files (and the step from documents to pictures) did not wait for the cloud files to be indexed.
        Assert.True(online.Count(store.Files.ContainsKey) < online.Count / 2, "local files waited for the cloud files");
        await WaitFor(() => online.All(store.Files.ContainsKey) && coordinator.Status.State == IndexingState.UpToDate, "cloud files indexed");
    }

    [Fact]
    public async Task AnErrorOnOneCloudFileDoesNotStopTheWalk()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { ArriveImmediately = true };
        var (online, local) = Files(tree, cloud, 3, 2);
        cloud.FailRequestFor = online[1];
        var store = new FakeStore();
        await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaPhase2Complete) == "1" && coordinator.Status.State == IndexingState.UpToDate,
            "walk complete despite the failing file");

        Assert.True(store.Files.ContainsKey(online[0]));
        Assert.True(store.Files.ContainsKey(online[2]));
        Assert.All(local, f => Assert.True(store.Files.ContainsKey(f)));
    }

    [Fact]
    public async Task GivingBackThatHangsIsSkippedAndTheQueueMovesOn()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { ArriveImmediately = true };
        var (online, local) = Files(tree, cloud, 6, 2);
        cloud.ReleaseHangs = path => path == online[0]; // waits on the sync client forever
        var store = new FakeStore();
        var options = new CloudPrefetchOptions
        {
            MaxFiles = 2, // the hung file holds one of only two slots
            PollInterval = TimeSpan.FromMilliseconds(20),
            StallTimeout = TimeSpan.FromMinutes(10),
            FreeBytes = _ => 1L << 40,
        };
        try
        {
            await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud), options);
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaPhase2Complete) == "1", "walk complete despite the hung give-back");

            Assert.All(online, f => Assert.True(store.Files.ContainsKey(f)));
            Assert.Equal(1, Volatile.Read(ref cloud.HungReleases));
            // The hung file stays on record, so the next start gives it back; the others are done with. The walk counts
            // as complete once every file is indexed, while giving the others back can still be under way, so wait for it.
            await WaitFor(() => store.LoadDownloads().Select(d => d.Path).SequenceEqual([online[0]]), "the files that did not hang given back");
            Assert.Equal([online[0]], store.LoadDownloads().Select(d => d.Path));
        }
        finally
        {
            cloud.Unhang();
        }
    }

    [Fact]
    public async Task GivingBackThatKeepsHangingStopsCloudDownloadsButTheWalkFinishes()
    {
        using var tree = new TempTree();
        var cloud = new FakeCloud { ArriveImmediately = true, ReleaseHangs = _ => true };
        var (online, local) = Files(tree, cloud, 8, 2);
        var store = new FakeStore();
        var options = new CloudPrefetchOptions
        {
            MaxFiles = 1,
            PollInterval = TimeSpan.FromMilliseconds(20),
            StallTimeout = TimeSpan.FromMinutes(10),
            FreeBytes = _ => 1L << 40,
        };
        try
        {
            await using var coordinator = Create([tree.Root], [.. online, .. local], store, cloud, new CloudAwareReader(cloud), options);
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaPhase2Complete) == "1", "walk complete");

            // Three hangs, then no more downloads: files it could not give back must not pile up on the disk.
            Assert.Equal(3, cloud.RequestLog.Count);
            Assert.Empty(cloud.Downloads);
            Assert.All(online, f => Assert.True(store.Files.ContainsKey(f)));
            Assert.Equal(online.Count - 3, online.Count(f => store.Files[f].Entry.ContentPending)); // by name for now
        }
        finally
        {
            cloud.Unhang();
        }
    }
}
