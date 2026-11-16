using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Valafind.Core.Tests.Storage;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>Regression tests for the indexing review findings.</summary>
public class IndexingCoordinatorReviewFixTests
{
    private static readonly CoordinatorTuning Tuning = CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, TimeSpan.Zero, true, TimeSpan.FromMilliseconds(25)),
    };

    private static IndexingCoordinator Create(IFileDiscovery discovery, IIndexStore store, FakeModel? model,
        FakeReader? reader = null, ValafindSettings? settings = null, CoordinatorTuning? tuning = null) =>
        new(discovery, reader ?? new FakeReader(), new FakeChunker(), model, store, new FakeMonitor(),
            settings ?? new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, tuning ?? Tuning);

    private static UserFileDiscovery Discovery(TempTree tree, ValafindSettings settings) =>
        new(settings, tree.Dir("home"), tree.P("data"), TimeProvider.System, _ => null);

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

    private static async Task RunUntilUpToDate(IndexingCoordinator coordinator, string what)
    {
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status is { State: IndexingState.UpToDate, DiscoveryComplete: true }, what);
    }

    // ------------------------------------------------------------------ sweep-dead-in-production

    [Fact]
    public async Task SqliteStoreSweepsFilesDeletedWhileNotRunning()
    {
        using var tree = new TempTree();
        using var db = new TempDatabase();
        var keep = tree.File("keep.txt", "keep");
        var gone = tree.File("sub/gone.txt", "gone");
        var discovery = new FakeDiscovery([tree.Root], [], [keep, gone]);
        using (var store = db.OpenStore("fake/4", 4, Valafind.Core.Storage.PathKeyPolicy.Current))
        {
            await using var coordinator = Create(discovery, store, new FakeModel());
            await RunUntilUpToDate(coordinator, "first run");
            Assert.Equal(2, store.GetStats().Files);
        }

        File.Delete(gone); // while Valafind is not running
        using (var store = db.OpenStore("fake/4", 4, Valafind.Core.Storage.PathKeyPolicy.Current))
        {
            Assert.IsAssignableFrom<IIndexedPathSource>(store);
            store.SetMeta(IndexingCoordinator.MetaLastFullScan, DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
            await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [keep]), store, new FakeModel());
            await RunUntilUpToDate(coordinator, "second run");
            await WaitFor(() => store.GetFingerprint(gone) is null, "deleted file swept");
            Assert.NotNull(store.GetFingerprint(keep));
        }
    }

    [Fact]
    public async Task SweepPagesThroughLargeIndexes()
    {
        using var tree = new TempTree();
        var store = new FakeStore();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1234; i++)
            store.UpsertFile(new FileEntry(tree.P($"ghost{i:D4}.txt"), 5, now, FileKind.Text, IndexPhase.All), []);
        var a = tree.File("a.txt", "alpha");
        store.SetMeta(IndexingCoordinator.MetaModelId, "fake/4");
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [a]), store, new FakeModel());
        await RunUntilUpToDate(coordinator, "up to date");
        await WaitFor(() => store.Files.Count == 1, "ghosts swept");
        Assert.True(store.Contains(a));
        Assert.True(store.PageRequests >= 3, $"expected paged listing, got {store.PageRequests} page(s)");
    }

    [Fact]
    public async Task SweepKeepsEntriesOfUnavailableRootsAndDropsRemovedRoots()
    {
        using var tree = new TempTree();
        var store = new FakeStore();
        var a = tree.File("home/a.txt", "alpha");
        var offline = tree.P("usb");       // configured root, currently unmounted (missing)
        var emptyMount = tree.Dir("nas");  // configured root, mount point present but empty
        var removed = tree.P("old");       // no longer a root
        var now = DateTimeOffset.UtcNow;
        foreach (var path in new[] { Path.Combine(offline, "x.txt"), Path.Combine(emptyMount, "y.txt"), Path.Combine(removed, "z.txt") })
            store.UpsertFile(new FileEntry(path, 5, now, FileKind.Text, IndexPhase.All), []);
        store.SetMeta(IndexingCoordinator.MetaModelId, "fake/4");

        var discovery = new FakeDiscovery([tree.P("home"), offline, emptyMount], [], [a]);
        await using var coordinator = Create(discovery, store, new FakeModel());
        await RunUntilUpToDate(coordinator, "up to date");
        await WaitFor(() => !store.Contains(Path.Combine(removed, "z.txt")), "entry of removed root swept");
        Assert.True(store.Contains(Path.Combine(offline, "x.txt")));
        Assert.True(store.Contains(Path.Combine(emptyMount, "y.txt")));
        Assert.True(store.Contains(a));
    }

    [Fact]
    public async Task WatcherOverflowRescanRemovesEntriesWhoseDeleteWasLost()
    {
        using var tree = new TempTree();
        var store = new FakeStore();
        var a = tree.File("a.txt", "alpha");
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [a]), store, new FakeModel());
        await RunUntilUpToDate(coordinator, "up to date");

        var lost = tree.P("sub/lost.txt"); // indexed, then deleted while the kernel queue overflowed
        store.UpsertFile(new FileEntry(lost, 5, DateTimeOffset.UtcNow, FileKind.Text, IndexPhase.All), []);
        coordinator.OnWatcherEvent(tree.Root, ChangeKind.Rescan);
        await WaitFor(() => !store.Contains(lost), "lost delete cleaned up");
        Assert.True(store.Contains(a));
    }

    // ------------------------------------------------------------------ root-change-no-rescan

    [Fact]
    public async Task ChangingRootsWalksNewRootsAtOnceAndSweepsRemovedOnes()
    {
        using var tree = new TempTree();
        var store = new FakeStore();
        var docs = tree.File("home/docs/a.txt", "alpha");
        var projects = tree.File("home/projects/b.txt", "beta");
        var secret = tree.File("home/projects/secret/c.txt", "gamma");
        var settings = new ValafindSettings { Roots = [tree.P("home/docs")] };
        await using (var coordinator = Create(Discovery(tree, settings), store, new FakeModel(), settings: settings))
            await RunUntilUpToDate(coordinator, "first run");
        Assert.True(store.Contains(docs));
        Assert.False(store.Contains(projects));

        // Settings: add a root. The last full scan is fresh, so only the configuration change can trigger the walk.
        settings = new ValafindSettings { Roots = [tree.P("home/docs"), tree.P("home/projects")] };
        await using (var coordinator = Create(Discovery(tree, settings), store, new FakeModel(), settings: settings))
        {
            await RunUntilUpToDate(coordinator, "second run");
            await WaitFor(() => store.Contains(projects) && store.Contains(secret), "new root indexed");
        }

        // Settings: remove the first root again.
        settings = new ValafindSettings { Roots = [tree.P("home/projects")] };
        await using (var coordinator = Create(Discovery(tree, settings), store, new FakeModel(), settings: settings))
        {
            await RunUntilUpToDate(coordinator, "third run");
            await WaitFor(() => !store.Contains(docs), "removed root swept");
        }

        // Settings: exclude a folder by name.
        Assert.True(store.Contains(secret));
        settings = new ValafindSettings { Roots = [tree.P("home/projects")], Excludes = ["secret"] };
        await using (var coordinator = Create(Discovery(tree, settings), store, new FakeModel(), settings: settings))
        {
            await RunUntilUpToDate(coordinator, "fourth run");
            await WaitFor(() => !store.Contains(secret), "newly excluded folder swept");
        }
        Assert.True(store.Contains(projects));
    }

    [Fact]
    public void ConfigurationFingerprintIgnoresOrderButNotContent()
    {
        var store = new FakeStore();
        IndexingCoordinator Make(string[] roots, List<string> excludes) =>
            Create(new FakeDiscovery(roots, [], []), store, new FakeModel(), settings: new ValafindSettings { Excludes = excludes });
        var a = Make([TestPaths.P("/a"), TestPaths.P("/b")], ["x", "y"]).ConfigurationFingerprint();
        Assert.Equal(a, Make([TestPaths.P("/b"), TestPaths.P("/a")], ["y", "x"]).ConfigurationFingerprint());
        Assert.NotEqual(a, Make([TestPaths.P("/a")], ["x", "y"]).ConfigurationFingerprint());
        Assert.NotEqual(a, Make([TestPaths.P("/a"), TestPaths.P("/b")], ["x"]).ConfigurationFingerprint());
    }

    // ------------------------------------------------------------------ daily-rescan-resets-progress

    [Fact]
    public async Task DailyRescanKeepsTheLastTotalsVisible()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 20).Select(i => tree.File($"f{i}.txt", $"content {i}")).ToList();
        var store = new FakeStore();
        var discovery = new FakeDiscovery([tree.Root], [], files);
        await using (var coordinator = Create(discovery, store, new FakeModel()))
            await RunUntilUpToDate(coordinator, "first run");

        store.SetMeta(IndexingCoordinator.MetaLastFullScan, DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        var statuses = new List<IndexStatus>();
        var reader = new FakeReader { Delay = TimeSpan.FromMilliseconds(5) };
        await using (var coordinator = Create(discovery, store, new FakeModel(), reader))
        {
            coordinator.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };
            File.WriteAllText(files[3], "edited, and longer than before");
            await RunUntilUpToDate(coordinator, "re-walk");
            await WaitFor(() => store.Get(files[3]).Entry.Size == new FileInfo(files[3]).Length, "edit picked up by the re-walk");
            Assert.Equal(20, coordinator.Status.AllDone);
            Assert.Equal(20, coordinator.Status.AllTotal);
            // Events are coalesced; the trailing one carries the final state.
            await WaitFor(() => { lock (statuses) return statuses.Count > 0 && statuses[^1].State == IndexingState.UpToDate; }, "final status event");
        }
        lock (statuses)
        {
            var started = statuses.Where(s => s.State != IndexingState.Starting).ToList();
            Assert.NotEmpty(started);
            Assert.All(started, s =>
            {
                Assert.NotEqual(IndexPhase.All, s.Phase);
                Assert.Equal(20, s.AllTotal);
                Assert.Equal(20, s.AllDone);
                Assert.True(s.DiscoveryComplete);
            });
        }
    }

    // ------------------------------------------------------------------ case-rename-never-updates-path

    [Fact]
    public async Task CaseOnlyRenameUpdatesTheStoredSpelling()
    {
        using var tree = new TempTree();
        var renamed = tree.File("Report.txt", "quarterly numbers");
        var info = new FileInfo(renamed);
        var oldSpelling = tree.P("report.txt");
        var store = new FakeStore(StringComparer.OrdinalIgnoreCase); // Windows / macOS key semantics
        store.UpsertFile(new FileEntry(oldSpelling, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            FileKind.Text, IndexPhase.All), []);
        var model = new FakeModel();
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], []), store, model);
        coordinator.OverrideThrottle(TimeSpan.FromMinutes(5));

        Assert.True(coordinator.ProcessFile(renamed, IndexPhase.Incremental, TestContext.Current.CancellationToken));
        Assert.Equal([renamed], store.Files.Keys.ToList());
        Assert.Contains(model.Calls.SelectMany(c => c), d => d.Title == "Report.txt");

        // Same spelling, same fingerprint: skipped.
        var calls = model.Calls.Count;
        Assert.True(coordinator.ProcessFile(renamed, IndexPhase.Incremental, TestContext.Current.CancellationToken));
        Assert.Equal(calls, model.Calls.Count);
    }

    [Fact]
    public void UnicodeNormalisationAloneIsNotARename()
    {
        var nfc = TestPaths.P("/home/u/Café.txt");
        var nfd = TestPaths.P("/home/u/Café.txt");
        Assert.False(IndexingCoordinator.IsDifferentSpelling(nfc, nfd));
        Assert.True(IndexingCoordinator.IsDifferentSpelling(TestPaths.P("/home/u/cafe.txt"), TestPaths.P("/home/u/Cafe.txt")));
        Assert.False(IndexingCoordinator.IsDifferentSpelling(TestPaths.P("/home/u/a.txt"), TestPaths.P("/home/u/a.txt")));
    }

    // ------------------------------------------------------------------ fingerprint-tolerance-misses-edits

    [Fact]
    public void SameSizeEditWithinTwoSecondsIsNotSkippedOnFineGrainedFileSystems()
    {
        var t0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);
        var stored = new FileFingerprint(100, t0);
        Assert.True(IndexingCoordinator.IsUnchanged(stored, 100, t0));
        Assert.False(IndexingCoordinator.IsUnchanged(stored, 100, t0.AddSeconds(1))); // typo fixed a second later
        Assert.False(IndexingCoordinator.IsUnchanged(stored, 100, t0.AddTicks(1)));
        Assert.False(IndexingCoordinator.IsUnchanged(stored, 101, t0));

        // FAT / SMB: one side truncated to whole (even) seconds is the same file.
        var whole = new DateTimeOffset(2026, 3, 1, 10, 0, 2, TimeSpan.Zero);
        Assert.True(IndexingCoordinator.IsUnchanged(new FileFingerprint(100, whole), 100, whole.AddTicks(-1_234_567)));
        Assert.True(IndexingCoordinator.IsUnchanged(new FileFingerprint(100, whole.AddTicks(-1_234_567)), 100, whole));
        Assert.False(IndexingCoordinator.IsUnchanged(new FileFingerprint(100, whole), 100, whole.AddSeconds(2)));
    }

    // ------------------------------------------------------------------ windows-dir-changed-recursive-rescan

    [Fact]
    public async Task ChangedEventOnADirectoryDoesNotRewalkIt()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var unseen = tree.File("sub/unseen.txt", "never listed by discovery"); // only a directory walk finds it
        var store = new FakeStore();
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [a]), store, new FakeModel());
        await RunUntilUpToDate(coordinator, "up to date");

        // Windows raises Changed for a directory whenever an entry inside it is created / deleted / renamed.
        coordinator.OnWatcherEvent(tree.P("sub"), ChangeKind.Changed);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.False(store.Contains(unseen));

        // Created (or moved in) still walks the directory.
        coordinator.OnWatcherEvent(tree.P("sub"), ChangeKind.Upsert);
        await WaitFor(() => store.Contains(unseen), "created directory walked");
    }

    // ------------------------------------------------------------------ SetInteractive

    [Fact]
    public async Task InteractiveSearchHoldsModelBatchesWithoutChangingTheStatus()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 10).Select(i => tree.File($"f{i}.txt", $"file {i}")).ToList();
        var store = new FakeStore();
        var model = new FakeModel();
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], files), store, model);
        coordinator.SetInteractive(true);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.Running, "worker running");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(model.Calls);
        Assert.Equal(IndexingState.Running, coordinator.Status.State); // not a misleading "system busy"

        coordinator.SetInteractive(false);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "resumed");
        Assert.Equal(files.Count, store.Files.Count);

        // Interactive again while idle, then a change arrives: it waits, and goes through once search ends.
        coordinator.SetInteractive(true);
        var late = tree.File("late.txt", "late");
        coordinator.OnWatcherEvent(late, ChangeKind.Upsert);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(store.Contains(late));
        coordinator.SetInteractive(false);
        await WaitFor(() => store.Contains(late), "change indexed after search");
    }

    [Fact]
    public async Task InteractiveFlagLeftOnTimesOut()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 5).Select(i => tree.File($"f{i}.txt", $"file {i}")).ToList();
        var store = new FakeStore();
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], files), store, new FakeModel(),
            tuning: Tuning with { InteractiveTimeout = TimeSpan.FromMilliseconds(300) });
        coordinator.SetInteractive(true); // e.g. launcher left open
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "indexed despite the stuck flag", 30);
        Assert.Equal(files.Count, store.Files.Count);
    }

    [Fact]
    public void DefaultBatchIsSmallSoAQueryNeverWaitsLong() =>
        Assert.Equal(2, CoordinatorTuning.Default.BatchSize);
}
