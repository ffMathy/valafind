using System.Collections.Concurrent;
using System.Diagnostics;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Valafind.Core.Tests.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public class InotifyTreeWatcherTests
{
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20))
                Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static UserFileDiscovery Discovery(TempTree tree) =>
        new(new ValafindSettings(), tree.Dir("home"), tree.P("data"), TimeProvider.System, _ => null);

    [Fact]
    public async Task ExcludedDirectoriesGetNoWatchesAndNewDirectoriesAreFollowed()
    {
        if (!OperatingSystem.IsLinux())
            return; // inotify is Linux-only; Windows and macOS keep FileSystemWatcher (no per-directory cost)

        using var tree = new TempTree();
        var discovery = Discovery(tree);
        tree.Dir("home/docs/sub");
        for (var i = 0; i < 50; i++)
        {
            tree.Dir($"home/proj/node_modules/pkg{i}/lib");
            tree.Dir($"home/.cache/c{i}");
            tree.Dir($"home/proj/.git/objects/{i:x2}");
        }
        tree.File("home/proj/app.csproj", "<Project />");
        tree.Dir("home/proj/bin/Debug"); // bin next to a project file

        var events = new ConcurrentQueue<(string Path, TreeEventKind Kind)>();
        var problems = new ConcurrentQueue<TreeWatchProblem>();
        using var watcher = new InotifyTreeWatcher(tree.P("home"), (d, p) => discovery.Rules.IsExcludedDirectory(d, p),
            (path, kind) => events.Enqueue((path, kind)), problems.Enqueue);
        watcher.Start();
        await watcher.Ready.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(4, watcher.WatchCount); // home, home/docs, home/docs/sub, home/proj — nothing excluded
        Assert.Empty(problems);

        // Events in excluded trees never reach us.
        tree.File("home/proj/node_modules/pkg1/lib/x.js", "x");
        tree.File("home/.cache/c1/blob", "x");

        // File events.
        var note = tree.File("home/docs/sub/note.txt", "hello");
        await WaitFor(() => events.Contains((note, TreeEventKind.Created)), "created");
        File.AppendAllText(note, " world");
        await WaitFor(() => events.Contains((note, TreeEventKind.Changed)), "changed");

        // A new directory is reported and watched, so files created inside it later are seen too.
        var fresh = tree.Dir("home/docs/fresh/inner");
        await WaitFor(() => events.Contains((tree.P("home/docs/fresh"), TreeEventKind.Created)), "directory created");
        var deep = tree.File("home/docs/fresh/inner/deep.txt", "deep");
        await WaitFor(() => events.Contains((deep, TreeEventKind.Created)), "file in new directory");
        Assert.Equal(6, watcher.WatchCount);

        // Moving a directory: old path deleted, new path created, and later events use the new path.
        Directory.Move(tree.P("home/docs/fresh"), tree.P("home/docs/moved"));
        await WaitFor(() => events.Contains((tree.P("home/docs/fresh"), TreeEventKind.Deleted)), "moved away");
        await WaitFor(() => events.Contains((tree.P("home/docs/moved"), TreeEventKind.Created)), "moved in");
        var after = tree.File("home/docs/moved/inner/after.txt", "after");
        await WaitFor(() => events.Contains((after, TreeEventKind.Created)), "file in moved directory");

        File.Delete(note);
        await WaitFor(() => events.Contains((note, TreeEventKind.Deleted)), "deleted");

        // A new excluded directory is not watched.
        tree.Dir("home/docs/node_modules/z");
        tree.File("home/docs/flag.txt", "sync point");
        await WaitFor(() => events.Contains((tree.P("home/docs/flag.txt"), TreeEventKind.Created)), "sync point");
        Assert.Equal(6, watcher.WatchCount);

        Assert.DoesNotContain(events, e => e.Path.Contains("node_modules", StringComparison.Ordinal) ||
                                            e.Path.Contains(".cache", StringComparison.Ordinal) ||
                                            e.Path.Contains(".git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoordinatorWatchesOnlyIndexedDirectories()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tree = new TempTree();
        tree.File("home/docs/a.txt", "alpha");
        for (var i = 0; i < 30; i++)
            tree.Dir($"home/.local/share/x{i}");
        var discovery = Discovery(tree);
        await using var coordinator = new IndexingCoordinator(discovery, new FakeReader(), new FakeChunker(), new FakeModel(),
            new FakeStore(), new FakeMonitor(), new ValafindSettings(), Microsoft.Extensions.Logging.Abstractions.NullLogger<IndexingCoordinator>.Instance,
            null, CoordinatorTuning.Default with
            {
                Debounce = TimeSpan.FromMilliseconds(100),
                IdlePoll = TimeSpan.FromMilliseconds(200),
                Throttle = new ThrottleOptions(30, 25, true, TimeSpan.FromMilliseconds(25)),
            });
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        await WaitFor(() => coordinator.Status.State == IndexingState.UpToDate, "up to date");
        Assert.Equal(2, await coordinator.WaitForWatchersAsync()); // home and home/docs
    }

    [Fact]
    public async Task DisposeNeverClosesTheDescriptorUnderAStuckWatcherThread()
    {
        if (!OperatingSystem.IsLinux())
            return; // inotify is Linux-only

        using var tree = new TempTree();
        tree.Dir("home/a/b");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The exclusion callback stands in for a directory enumeration hung on a dead network mount.
        var watcher = new InotifyTreeWatcher(tree.P("home"), (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return false;
        }, (_, _) => { }, _ => { });
        watcher.DisposeJoinTimeout = TimeSpan.FromMilliseconds(100);
        watcher.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

        watcher.Dispose(); // join times out: the thread is still inside AddTree
        Assert.False(watcher.DescriptorClosed);
        Assert.Equal("anon_inode:inotify", new FileInfo($"/proc/self/fd/{watcher.Descriptor}").LinkTarget);

        release.Set();
        await WaitFor(() => OperatingSystem.IsLinux() && watcher.DescriptorClosed, "the watcher thread closes its descriptor on exit");
    }

    [Fact]
    public async Task ACallbackThatThrowsStopsTheWatcherWithoutCrashing()
    {
        if (!OperatingSystem.IsLinux())
            return; // inotify is Linux-only

        using var tree = new TempTree();
        tree.Dir("home/a");
        using var watcher = new InotifyTreeWatcher(tree.P("home"),
            (_, _) => throw new InvalidOperationException("boom"), (_, _) => { }, _ => { });
        watcher.Start();
        await watcher.Ready.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await WaitFor(() => OperatingSystem.IsLinux() && watcher.DescriptorClosed, "the watcher thread exits and closes its descriptor");
    }
}
