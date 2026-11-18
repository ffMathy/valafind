using System.Diagnostics;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Valafind.Core.Tests.Indexing;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public class FsEventsHistoryTests
{
    [Theory]
    [InlineData(0x01u, (int)ChangeKind.Rescan)]                 // MustScanSubDirs
    [InlineData(0x01u | 0x02u, (int)ChangeKind.Rescan)]         // + UserDropped
    [InlineData(0x10000u | 0x1000u, (int)ChangeKind.Changed)]   // file modified
    [InlineData(0x10000u | 0x200u, (int)ChangeKind.Changed)]    // file removed
    [InlineData(0x10000u | 0x800u, (int)ChangeKind.Changed)]    // file renamed
    [InlineData(0x40000u | 0x100u, (int)ChangeKind.Changed)]    // symlink created
    [InlineData(0x20000u | 0x100u, (int)ChangeKind.Upsert)]     // directory created
    [InlineData(0x20000u | 0x200u, (int)ChangeKind.Upsert)]     // directory removed
    [InlineData(0x20000u | 0x800u, (int)ChangeKind.Upsert)]     // directory renamed
    [InlineData(0u, (int)ChangeKind.Upsert)]                    // "something changed in this directory"
    public void ClassifiesEvents(uint flags, int expected) =>
        Assert.Equal((ChangeKind)expected, FsEventsHistory.Classify(flags));

    [Theory]
    [InlineData(0x20000u | 0x400u)]  // directory metadata only
    [InlineData(0x20u)]              // root changed
    [InlineData(0x40u)]              // mount
    [InlineData(0x80u)]              // unmount
    public void IgnoresEventsWithNothingToIndex(uint flags) =>
        Assert.Null(FsEventsHistory.Classify(flags));

    [Fact]
    public void ParsesCheckpoints()
    {
        Assert.True(FsEventsHistory.TryParse("fsevents-v1\n42\nUUID-A\t/Users/a\nUUID-B\t/Volumes/x y", out var since, out var roots));
        Assert.Equal(42ul, since);
        Assert.Equal([("UUID-A", "/Users/a"), ("UUID-B", "/Volumes/x y")], roots);

        Assert.False(FsEventsHistory.TryParse("fsevents-v2\n42", out _, out _));
        Assert.False(FsEventsHistory.TryParse("fsevents-v1\nnot-a-number", out _, out _));
        Assert.False(FsEventsHistory.TryParse("fsevents-v1\n42\nno-tab", out _, out _));
        Assert.False(FsEventsHistory.TryParse("", out _, out _));
    }

    [Fact]
    public void ReplaysChangesMadeAfterTheCheckpoint()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("FSEvents is macOS-only.");
            return;
        }
        using var tree = new TempTree();
        var root = tree.Dir("root"); // under /var/folders: FSEvents reports /private/var/folders, mapped back
        var before = Path.Combine(root, "before.txt");
        File.WriteAllText(before, "before");

        var history = new FsEventsHistory(TimeSpan.FromSeconds(30));
        // FSEvents writes its log asynchronously: an event made just before the checkpoint can carry a later id.
        Thread.Sleep(1500);
        var checkpoint = history.Capture([root]);
        Assert.NotNull(checkpoint);

        var created = Path.Combine(root, "sub", "created.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(created)!);
        File.WriteAllText(created, "created");
        File.Delete(before);
        Thread.Sleep(1500);

        var changes = new List<(string Path, ChangeKind Kind)>();
        var sw = Stopwatch.StartNew();
        Assert.True(history.TryReplay(checkpoint, [root], (p, k) => { lock (changes) changes.Add((p, k)); }, TestContext.Current.CancellationToken));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));

        lock (changes)
        {
            Assert.Contains((created, ChangeKind.Changed), changes);
            Assert.Contains((before, ChangeKind.Changed), changes);
        }

        // Another set of roots is not what the checkpoint covers.
        Assert.False(history.TryReplay(checkpoint, [root, tree.Dir("other")], (_, _) => { }, TestContext.Current.CancellationToken));
    }
}
