using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Indexing;

public class IndexingCoordinatorTests
{
    private static readonly CoordinatorTuning Tuning = CoordinatorTuning.Default with
    {
        Debounce = TimeSpan.FromMilliseconds(100),
        IdlePoll = TimeSpan.FromMilliseconds(200),
        ModelBackoffUnit = TimeSpan.FromMilliseconds(1),
        Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
        Pace = new IndexingPace(maxConcurrency: 1, processorCount: 1), // one file at a time: these tests check order
    };

    private static IndexingCoordinator Create(IFileDiscovery discovery, FakeStore store, FakeModel? model, FakeMonitor? monitor = null,
        FakeReader? reader = null, ValafindSettings? settings = null) =>
        new(discovery, reader ?? new FakeReader(), new FakeChunker(), model, store, monitor ?? new FakeMonitor(),
            settings ?? new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, Tuning);

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

    [Fact]
    public async Task MissingModelReportsModelUnavailableAndDoesNothing()
    {
        using var tree = new TempTree();
        var store = new FakeStore();
        var file = tree.File("a.txt");
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [file], [file]), store, model: null);
        Assert.Equal(IndexingState.ModelUnavailable, coordinator.Status.State);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(IndexingState.ModelUnavailable, coordinator.Status.State);
        Assert.Empty(store.Snapshot());
        Assert.Empty(store.Meta);
    }

    [Fact]
    public async Task IndexesPhase1BeforePhase2AndSkipsAlreadyIndexedFiles()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var b = tree.File("b.md", new string('b', 520)); // 11 chunks → two model batches
        var c = tree.File("sub/c.txt", "gamma");
        var d = tree.File("sub/deeper/pic.png", "not really a png");
        var e = tree.File("unknown.xyz", "opaque");
        var store = new FakeStore();
        var model = new FakeModel();
        var discovery = new FakeDiscovery([tree.Root], frequent: [c, a], all: [a, b, c, d, e]);
        var statuses = new List<IndexStatus>();
        await using var coordinator = Create(discovery, store, model);
        coordinator.StatusChanged += (_, s) => { lock (statuses) statuses.Add(s); };

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");

        var upserts = store.Snapshot().Where(l => l.Op == "upsert").ToList();
        Assert.Equal([c, a, b, d], upserts.Select(u => u.Path));
        Assert.Equal([IndexPhase.Frequent, IndexPhase.Frequent, IndexPhase.All, IndexPhase.All], upserts.Select(u => u.Phase!.Value));

        // Chunked text: every chunk embedded, batches of at most 8.
        var (bEntry, bChunks) = store.Get(b);
        Assert.Equal(FileKind.Text, bEntry.Kind);
        Assert.Equal(11, bChunks.Count);
        Assert.All(model.Calls, call => Assert.InRange(call.Count, 1, 8));
        Assert.Equal(new string('b', 50), bChunks[0].Text);
        Assert.Equal(4, bChunks[0].Vector.Length);

        // Name-only entry for an image: one empty chunk, embedded from the relative path.
        var (dEntry, dChunks) = store.Get(d);
        Assert.Equal(FileKind.Image, dEntry.Kind);
        var only = Assert.Single(dChunks);
        Assert.Equal(string.Empty, only.Text);
        Assert.Contains(model.Calls.SelectMany(x => x), doc => doc.Title == "pic.png" && doc.Text == "sub / deeper / pic.png");

        // Unknown unreadable file is skipped.
        Assert.False(store.Contains(e));

        var status = coordinator.Status;
        Assert.Equal(IndexPhase.Incremental, status.Phase);
        Assert.Equal(2, status.FrequentDone);
        Assert.Equal(2, status.FrequentTotal);
        Assert.Equal(5, status.AllDone);
        Assert.Equal(5, status.AllTotal);
        Assert.True(status.DiscoveryComplete);
        Assert.Null(status.CurrentFile);
        Assert.Equal("1", store.GetMeta(IndexingCoordinator.MetaPhase1Complete));
        Assert.Equal("1", store.GetMeta(IndexingCoordinator.MetaPhase2Complete));
        Assert.NotNull(store.GetMeta(IndexingCoordinator.MetaLastFullScan));
        Assert.Equal("fake/4", store.GetMeta(IndexingCoordinator.MetaModelId));
        // Coalescing may drop intermediate states, but the trailing event always carries the final one.
        await WaitFor(() => { lock (statuses) return statuses.Count > 0 && statuses[^1].State == IndexingState.UpToDate; }, "final status event");
    }

    [Fact]
    public async Task IndexesSeveralFilesAtOnceWhenThePaceAllows()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 12).Select(i => tree.File($"doc{i}.txt", "text " + i)).ToList();
        var store = new FakeStore();
        var reader = new FakeReader { Delay = TimeSpan.FromMilliseconds(150) };
        var tuning = Tuning with { Pace = new IndexingPace(maxConcurrency: 4, processorCount: 4, initialEffort: 4) };
        await using var coordinator = new IndexingCoordinator(new FakeDiscovery([tree.Root], [], files), reader, new FakeChunker(),
            new FakeModel(), store, new FakeMonitor(), new ValafindSettings(), NullLogger<IndexingCoordinator>.Instance, null, tuning);

        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");

        Assert.All(files, f => Assert.True(store.Contains(f)));
        Assert.Equal(12, coordinator.Status.AllDone);
        Assert.InRange(reader.PeakConcurrentReads, 2, 4);
    }

    [Fact]
    public async Task RestartSkipsCompletedPhasesAndUnchangedFiles()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 20).Select(i => tree.File($"f{i}.txt", $"content {i}")).ToList();
        var store = new FakeStore();
        var discovery = new FakeDiscovery([tree.Root], files.Take(3).ToList(), files);
        var first = new FakeModel();
        await using (var coordinator = Create(discovery, store, first))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "first run");
        }
        Assert.Equal(20, first.EmbeddedDocuments);

        // Fresh scan marker → nothing to do at all.
        var second = new FakeModel();
        await using (var coordinator = Create(discovery, store, second))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "second run");
            Assert.Equal(3, coordinator.Status.FrequentDone);
            Assert.Equal(20, coordinator.Status.AllTotal);
        }
        Assert.Empty(second.Calls);

        // Stale scan marker → Phase 2 re-walks, but fingerprints skip every unchanged file.
        store.SetMeta(IndexingCoordinator.MetaLastFullScan, DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
        File.WriteAllText(files[7], "changed content that is longer");
        var third = new FakeModel();
        var reader = new FakeReader();
        await using (var coordinator = Create(discovery, store, third, reader: reader))
        {
            await coordinator.StartAsync(TestContext.Current.CancellationToken);
            await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate && coordinator.Status.DiscoveryComplete, "third run");
            Assert.Equal(20, coordinator.Status.AllDone);
            // The status carries the store's live per-type counts.
            Assert.Equal(store.GetStats().Files, coordinator.Status.IndexedByKind.Values.Sum());
        }
        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, third.EmbeddedDocuments);
        Assert.Equal(IndexPhase.Incremental, store.Get(files[7]).Entry.Phase);
    }

    [Fact]
    public async Task ModelChangeRedoesAllPhases()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var store = new FakeStore();
        store.SetMeta(IndexingCoordinator.MetaModelId, "old-model");
        store.SetMeta(IndexingCoordinator.MetaPhase1Complete, "1");
        store.SetMeta(IndexingCoordinator.MetaPhase2Complete, "1");
        store.SetMeta(IndexingCoordinator.MetaLastFullScan, DateTimeOffset.UtcNow.ToString("O"));
        var model = new FakeModel();
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [a], [a]), store, model);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        Assert.True(store.Contains(a));
        Assert.Equal("fake/4", store.GetMeta(IndexingCoordinator.MetaModelId));
    }

    [Fact]
    public async Task StaleEntriesAreSweptAfterPhase2()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt", "alpha");
        var store = new FakeStore();
        var ghost = tree.P("ghost.txt");
        store.UpsertFile(new FileEntry(ghost, 5, DateTimeOffset.UtcNow, FileKind.Text, IndexPhase.All), []);
        store.SetMeta(IndexingCoordinator.MetaModelId, "fake/4");
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [a]), store, new FakeModel());
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        Assert.True(store.Contains(a));
        Assert.False(store.Contains(ghost));
    }

    [Fact]
    public async Task RepeatedModelFailuresSkipTheFileAndContinue()
    {
        using var tree = new TempTree();
        var bad = tree.File("bad.txt", "poison");
        var good = tree.File("good.txt", "fine");
        var store = new FakeStore();
        var model = new FakeModel { FailWhen = d => d.Text == "poison" };
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], [bad, good]), store, model);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        Assert.False(store.Contains(bad));
        Assert.True(store.Contains(good));
        Assert.Equal(3, model.Calls.Count(c => c[0].Text == "poison")); // retried with back-off, then skipped
    }

    [Fact]
    public async Task BatteryPauseStopsProgressAndResumeContinues()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 60).Select(i => tree.File($"f{i:D2}.txt", $"file {i}")).ToList();
        var store = new FakeStore();
        var monitor = new FakeMonitor();
        var reader = new FakeReader { Delay = TimeSpan.FromMilliseconds(15) };
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], files), store, new FakeModel(), monitor, reader);
        var ct = TestContext.Current.CancellationToken;
        await coordinator.StartAsync(ct);
        await WaitFor(() => coordinator.Status.AllDone >= 5, "some progress");

        monitor.OnBattery = true; // unplugged (system load never pauses; only the user and the battery rule do)
        await WaitFor(() => coordinator.Status.State == IndexingState.PausedOnBattery, "paused");
        // At most the file in flight when the gate closed may still complete.
        var pausedAt = store.Snapshot().Count;
        await WaitFor(() => (Volatile.Read(ref reader.Reads), store.Snapshot().Count) is var (reads, count) && reads == count && count >= pausedAt,
            "in-flight file settled");
        pausedAt = store.Snapshot().Count;
        await Task.Delay(500, ct);
        Assert.Equal(pausedAt, store.Snapshot().Count);
        Assert.True(pausedAt < files.Count);

        monitor.OnBattery = false;
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "finished after resume");
        Assert.Equal(files.Count, store.Files.Count);
    }

    [Fact]
    public async Task UserPauseAndOverride()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 40).Select(i => tree.File($"f{i:D2}.txt", $"file {i}")).ToList();
        var store = new FakeStore();
        var monitor = new FakeMonitor();
        var reader = new FakeReader { Delay = TimeSpan.FromMilliseconds(15) };
        await using var coordinator = Create(new FakeDiscovery([tree.Root], [], files), store, new FakeModel(), monitor, reader,
            new ValafindSettings { UserPaused = true });
        var ct = TestContext.Current.CancellationToken;
        await coordinator.StartAsync(ct);
        await WaitFor(() => coordinator.Status.State == IndexingState.PausedByUser, "paused by user");
        await Task.Delay(300, ct);
        Assert.Empty(store.Snapshot());

        monitor.OnBattery = true; // on battery too…
        coordinator.SetUserPaused(false);
        await WaitFor(() => coordinator.Status.State == IndexingState.PausedOnBattery, "on battery");
        coordinator.OverrideThrottle(TimeSpan.FromMinutes(5)); // …but the user said "resume now"
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "done under override");
        Assert.Equal(files.Count, store.Files.Count);
    }

    [Fact]
    public async Task WatcherPicksUpCreatedChangedAndDeletedFiles()
    {
        using var tree = new TempTree();
        var home = tree.Dir("home");
        var recent = tree.File("home/Documents/recent.txt", "recent words");
        var existing = tree.File("home/notes/existing.txt", "existing words");
        var store = new FakeStore();
        var discovery = new UserFileDiscovery(new ValafindSettings(), home, tree.P("data"), TimeProvider.System, _ => null);
        await using var coordinator = Create(discovery, store, new FakeModel());
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "initial indexing");
        Assert.Equal(IndexPhase.Frequent, store.Get(recent).Entry.Phase);
        Assert.Equal(IndexPhase.All, store.Get(existing).Entry.Phase);

        var created = tree.File("home/notes/new.txt", "brand new");
        await WaitFor(() => store.Contains(created), "created file indexed");
        Assert.Equal(IndexPhase.Incremental, store.Get(created).Entry.Phase);

        File.WriteAllText(existing, "existing words, now edited and longer");
        await WaitFor(() => store.Get(existing).Entry.Size == new FileInfo(existing).Length, "changed file re-indexed");

        tree.File("home/proj/node_modules/pkg/index.txt", "ignored");
        tree.File("home/.cache/x.txt", "ignored");

        File.Delete(created);
        await WaitFor(() => !store.Contains(created), "deleted file removed");

        // Moving a directory in from outside the root indexes its contents.
        var outside = tree.File("outside/folder/inner.txt", "inner");
        Directory.Move(tree.P("outside/folder"), tree.P("home/moved"));
        await WaitFor(() => store.Contains(tree.P("home/moved/inner.txt")), "moved-in directory indexed");
        Assert.False(File.Exists(outside));

        // Deleting a directory removes everything under it.
        Directory.Delete(tree.P("home/notes"), recursive: true);
        await WaitFor(() => !store.Contains(existing), "deleted directory removed");

        Assert.DoesNotContain(store.Snapshot(), l => l.Path.Contains("node_modules", StringComparison.Ordinal) || l.Path.Contains(".cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusEventsAreCoalesced()
    {
        using var tree = new TempTree();
        var files = Enumerable.Range(0, 200).Select(i => tree.File($"f{i:D3}.txt", $"{i}")).ToList();
        var store = new FakeStore();
        var events = 0;
        await using var coordinator = Create(new FakeDiscovery([tree.Root], files.Take(50).ToList(), files), store, new FakeModel());
        coordinator.StatusChanged += (_, _) => Interlocked.Increment(ref events);
        var sw = Stopwatch.StartNew();
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "done");
        var elapsed = sw.Elapsed;
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.InRange(events, 1, (int)(elapsed.TotalSeconds * 4) + 4);
        Assert.Equal(200, store.Files.Count);
    }

    [Fact]
    public async Task DisposeStopsAPausedWorkerPromptly()
    {
        using var tree = new TempTree();
        var a = tree.File("a.txt");
        var monitor = new FakeMonitor { OnBattery = true };
        var coordinator = Create(new FakeDiscovery([tree.Root], [a], [a]), new FakeStore(), new FakeModel(), monitor);
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.PausedOnBattery, "paused");
        var sw = Stopwatch.StartNew();
        await coordinator.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        await coordinator.DisposeAsync(); // idempotent
    }

    [Fact]
    public void NameOnlyTextUsesPathRelativeToRoot()
    {
        using var tree = new TempTree();
        var discovery = new FakeDiscovery([tree.Root, tree.P("a")], [], []);
        var coordinator = Create(discovery, new FakeStore(), new FakeModel());
        Assert.Equal("b / c.png", coordinator.NameOnlyText(tree.P("a/b/c.png")));
        Assert.Equal("x.png", coordinator.NameOnlyText(tree.P("x.png")));
    }
}

public class PendingChangesTests
{
    [Fact]
    public void DebouncesPerPathAndOrdersDeletesFirst()
    {
        var time = new ManualTimeProvider();
        var pending = new PendingChanges(time, TimeSpan.FromSeconds(2), StringComparer.Ordinal);
        pending.Add("/a", ChangeKind.Upsert);
        time.Advance(TimeSpan.FromSeconds(1));
        pending.Add("/a", ChangeKind.Upsert); // re-arms the debounce
        pending.Add("/b", ChangeKind.Delete);
        pending.Add("/root", ChangeKind.Rescan);
        Assert.Equal(TimeSpan.FromSeconds(2), pending.NextDueIn());
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Empty(pending.TakeDue());
        time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(
            [new DueChange("/b", ChangeKind.Delete), new DueChange("/a", ChangeKind.Upsert), new DueChange("/root", ChangeKind.Rescan)],
            pending.TakeDue());
        Assert.Equal(0, pending.Count);
        Assert.Null(pending.NextDueIn());
    }

    [Fact]
    public void LastEventKindWins()
    {
        var time = new ManualTimeProvider();
        var pending = new PendingChanges(time, TimeSpan.Zero, StringComparer.Ordinal);
        pending.Add("/a", ChangeKind.Upsert);
        pending.Add("/a", ChangeKind.Delete);
        pending.Add("/b", ChangeKind.Delete);
        pending.Add("/b", ChangeKind.Upsert);
        Assert.Equal([new DueChange("/a", ChangeKind.Delete), new DueChange("/b", ChangeKind.Upsert)], pending.TakeDue());
    }
}
