using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

/// <summary>The check for changes made while Valafind was not running, and the cheap walk it relies on.</summary>
public class StartupCheckTests
{
    private static readonly CoordinatorTuning Tuning = CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
        Pace = new IndexingPace(maxConcurrency: 1, processorCount: 1),
        StartupCheckDelay = TimeSpan.Zero,
        CheckpointSettle = TimeSpan.FromMilliseconds(300),
        History = null,
    };

    private static IndexingCoordinator Create(IFileDiscovery discovery, FakeStore store, CoordinatorTuning tuning, FakeReader? reader = null) =>
        new(discovery, reader ?? new FakeReader(), new FakeChunker(), new FakeModel(), store, new FakeMonitor(),
            new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, tuning);

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 30)
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

    /// <summary>Indexes <paramref name="files"/> once, as the run before the restart.</summary>
    private static async Task FirstRun(IFileDiscovery discovery, FakeStore store, CoordinatorTuning? tuning = null)
    {
        await using var coordinator = Create(discovery, store, tuning ?? Tuning);
        await RunUntilUpToDate(coordinator, "first run");
    }

    [Fact]
    public async Task RestartPicksUpOfflineChangesAfterTheDelay()
    {
        using var tree = new TempTree();
        var edited = tree.File("edited.txt", "before");
        var deleted = tree.File("deleted.txt", "doomed");
        var kept = tree.File("kept.txt", "kept");
        var store = new FakeStore();
        await FirstRun(new FakeStatDiscovery([tree.Root], [edited, deleted, kept]), store);

        // While Valafind is not running (the daily re-walk is not due):
        File.WriteAllText(edited, "after, and longer");
        File.Delete(deleted);
        var added = tree.File("added.txt", "new");
        var discovery = new FakeStatDiscovery([tree.Root], [edited, deleted, kept, added]);

        var reader = new FakeReader();
        await using var coordinator = Create(discovery, store, Tuning with { StartupCheckDelay = TimeSpan.FromSeconds(2) }, reader);
        var started = Stopwatch.StartNew();
        await RunUntilUpToDate(coordinator, "restart");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1.5), "the restart itself should not wait for the check");
        Assert.Equal(0, reader.Reads); // nothing yet: the check waits for its delay

        await WaitFor(() => store.Contains(added) && !store.Contains(deleted) &&
                            store.Get(edited).Entry.Size == new FileInfo(edited).Length, "offline changes picked up");
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(2), "the check ran before its delay");
        Assert.Equal(2, reader.Reads); // the edited and the added file; the unchanged one is not read
    }

    [Fact]
    public async Task WalkChecksUnchangedFilesInBatchesWithoutPerFileLookups()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 600).Select(i => tree.File($"d{i / 100}/f{i:D3}.txt", $"content {i}")).ToList();
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([tree.Root], files);
        await FirstRun(discovery, store);

        File.WriteAllText(files[123], "edited, and longer than before");
        store.EntryLookups = 0;
        store.BatchLookups = 0;
        var reader = new FakeReader();
        await using var coordinator = Create(discovery, store, Tuning, reader);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(files[123]).Entry.Size == new FileInfo(files[123]).Length, "edit picked up");
        await WaitFor(() => coordinator.Status is { State: IndexingState.UpToDate, AllDone: 600 }, "walk finished");

        Assert.Equal(1, reader.Reads);
        // 600 files in batches of 256: three queries (a slow walk may send a part-filled batch after a second).
        Assert.InRange(store.BatchLookups, 3, 10);
        // Only the edited file took the per-file check (fresh stat and lookup) before it was indexed.
        Assert.Equal(1, store.EntryLookups);
    }

    [Fact]
    public async Task WalkWithoutListingStatsStillChecksEveryFile()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 30).Select(i => tree.File($"f{i:D2}.txt", $"content {i}")).ToList();
        var store = new FakeStore();
        var discovery = new FakeDiscovery([tree.Root], [], files); // no IFileStatDiscovery
        await FirstRun(discovery, store);

        File.WriteAllText(files[4], "edited, and longer than before");
        var reader = new FakeReader();
        await using var coordinator = Create(discovery, store, Tuning, reader);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(files[4]).Entry.Size == new FileInfo(files[4]).Length, "edit picked up");
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task ReplaysTheChangeHistoryInsteadOfWalking()
    {
        using var tree = new TempTree();
        var reported = tree.File("reported.txt", "before");
        var unreported = tree.File("unreported.txt", "before");
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([tree.Root], [reported, unreported]);
        var history = new FakeHistory();
        var tuning = Tuning with { History = history };
        await FirstRun(discovery, store, tuning);
        store.SetMeta(IndexingCoordinator.MetaChangeCheckpoint, "cp-0"); // as saved by the previous run
        const string checkpoint = "cp-0";

        File.WriteAllText(reported, "after, and longer");
        File.WriteAllText(unreported, "after, and longer");
        history.Changes.Add((reported, ChangeKind.Changed));
        var walksBefore = Volatile.Read(ref discovery.Walks);
        await using var coordinator = Create(discovery, store, tuning);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(reported).Entry.Size == new FileInfo(reported).Length, "reported change indexed");
        await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaChangeCheckpoint) != checkpoint, "new checkpoint saved");

        lock (history.Replayed)
            Assert.Equal([checkpoint], history.Replayed);
        // No walk. (Not checked through `unreported`: on macOS the real file watcher, started before the check, can
        // report writes made just before it started, and then indexes that file without any walk.)
        Assert.Equal(walksBefore, Volatile.Read(ref discovery.Walks));
    }

    [Fact]
    public async Task WalksWhenTheHistoryCannotAnswer()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "before");
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([tree.Root], [a]);
        var history = new FakeHistory { CanReplay = false };
        var tuning = Tuning with { History = history };
        await FirstRun(discovery, store, tuning);
        store.SetMeta(IndexingCoordinator.MetaChangeCheckpoint, "cp-0"); // as saved by the previous run

        File.WriteAllText(a, "after, and longer");
        await using var coordinator = Create(discovery, store, tuning);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(a).Entry.Size == new FileInfo(a).Length, "change found by the walk");
        lock (history.Replayed)
            Assert.Single(history.Replayed);
    }

    [Fact]
    public async Task WalksWhenTheDailyRescanIsDueEvenWithHistory()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "before");
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([tree.Root], [a]);
        var history = new FakeHistory();
        var tuning = Tuning with { History = history };
        await FirstRun(discovery, store, tuning);
        store.SetMeta(IndexingCoordinator.MetaChangeCheckpoint, "cp-0"); // as saved by the previous run

        store.SetMeta(IndexingCoordinator.MetaLastFullScan, DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        File.WriteAllText(a, "after, and longer");
        await using var coordinator = Create(discovery, store, tuning);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(a).Entry.Size == new FileInfo(a).Length, "change found by the walk");
        lock (history.Replayed)
            Assert.Empty(history.Replayed);
    }

    [Fact]
    public async Task KeepsTheOldCheckpointUntilTheStartupCheckHasRun()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([tree.Root], [a]);
        var history = new FakeHistory();
        await FirstRun(discovery, store, Tuning with { History = history });
        store.SetMeta(IndexingCoordinator.MetaChangeCheckpoint, "cp-0"); // as saved by the previous run
        const string checkpoint = "cp-0";

        var tuning = Tuning with { History = history, StartupCheckDelay = TimeSpan.FromSeconds(2), CheckpointSettle = TimeSpan.Zero };
        await using var coordinator = Create(discovery, store, tuning);
        await RunUntilUpToDate(coordinator, "restart");
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        // Idle, but the offline changes have not been replayed yet: replacing the checkpoint now would lose them.
        Assert.Equal(checkpoint, store.GetMeta(IndexingCoordinator.MetaChangeCheckpoint));
        lock (history.Replayed)
            Assert.Empty(history.Replayed);

        await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaChangeCheckpoint) != checkpoint, "checkpoint after the check");
        lock (history.Replayed)
            Assert.Equal([checkpoint], history.Replayed);
    }

    [Fact]
    public async Task SavesNoNewCheckpointWhileNothingHappens()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var store = new FakeStore();
        var history = new FakeHistory();
        await using var coordinator = Create(new FakeStatDiscovery([tree.Root], [a]), store,
            Tuning with { History = history, CheckpointSettle = TimeSpan.FromMilliseconds(50) });
        await RunUntilUpToDate(coordinator, "first run");
        await WaitFor(() => store.GetMeta(IndexingCoordinator.MetaChangeCheckpoint) is not null, "checkpoint saved");
        var captures = history.Captures;
        await Task.Delay(1000, TestContext.Current.CancellationToken); // several idle polls
        Assert.Equal(captures, history.Captures);
    }

    [Fact]
    public async Task HistoryRescanAboveTheRootsRescansTheRoots()
    {
        using var tree = new TempTree();
        var root = tree.Dir("home");
        var a = tree.File("home/a.txt", "before");
        var store = new FakeStore();
        var discovery = new FakeStatDiscovery([root], [a]);
        var history = new FakeHistory();
        var tuning = Tuning with { History = history };
        await FirstRun(discovery, store, tuning);
        store.SetMeta(IndexingCoordinator.MetaChangeCheckpoint, "cp-0"); // as saved by the previous run

        File.WriteAllText(a, "after, and longer");
        history.Changes.Add((tree.Root, ChangeKind.Rescan)); // FSEvents lost detail above the root
        history.Changes.Add((Path.Combine(tree.Root, "elsewhere.txt"), ChangeKind.Changed)); // outside every root
        await using var coordinator = Create(discovery, store, tuning);
        await RunUntilUpToDate(coordinator, "restart");
        await WaitFor(() => store.Get(a).Entry.Size == new FileInfo(a).Length, "root rescanned");
        Assert.False(store.Contains(Path.Combine(tree.Root, "elsewhere.txt")));
    }
}
