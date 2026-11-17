using Valafind.Core.Indexing;
using Xunit;
using static Valafind.Core.Tests.TestPaths;

namespace Valafind.Core.Tests.Indexing;

public class PendingChangesReviewFixTests
{
    [Fact]
    public void LatestSpellingWinsOnCaseInsensitiveComparers()
    {
        var pending = new PendingChanges(new ManualTimeProvider(), TimeSpan.Zero, StringComparer.OrdinalIgnoreCase);
        pending.Add(P("/d/report.docx"), ChangeKind.Delete);   // rename report.docx → Report.docx
        pending.Add(P("/d/Report.docx"), ChangeKind.Upsert);
        Assert.Equal([new DueChange(P("/d/Report.docx"), ChangeKind.Upsert)], pending.TakeDue());
    }

    [Fact]
    public void ChangedNeverDowngradesAPendingCreate()
    {
        var pending = new PendingChanges(new ManualTimeProvider(), TimeSpan.Zero, StringComparer.Ordinal);
        pending.Add(P("/d/new"), ChangeKind.Upsert);  // directory created …
        pending.Add(P("/d/new"), ChangeKind.Changed); // … and its last-write time bumped by the files put into it
        pending.Add(P("/d/x.txt"), ChangeKind.Changed);
        Assert.Equal([new DueChange(P("/d/new"), ChangeKind.Upsert), new DueChange(P("/d/x.txt"), ChangeKind.Changed)], pending.TakeDue());
    }

    [Fact]
    public void EventsBelowAPendingRescanAreDropped()
    {
        var pending = new PendingChanges(new ManualTimeProvider(), TimeSpan.Zero, StringComparer.Ordinal);
        pending.Add(P("/root/a/x.txt"), ChangeKind.Upsert);
        pending.Add(P("/other/y.txt"), ChangeKind.Upsert);
        pending.Add(P("/root"), ChangeKind.Rescan);
        pending.Add(P("/root/a/z.txt"), ChangeKind.Delete);
        pending.Add(P("/root/a"), ChangeKind.Rescan); // covered by the root rescan
        Assert.Equal(2, pending.Count);
        Assert.Equal([new DueChange(P("/other/y.txt"), ChangeKind.Upsert), new DueChange(P("/root"), ChangeKind.Rescan)], pending.TakeDue());
    }

    [Fact]
    public void OverflowCollapsesIntoRootRescans()
    {
        var root = P("/home/u");
        var pending = new PendingChanges(new ManualTimeProvider(), TimeSpan.Zero, StringComparer.Ordinal,
            path => path.StartsWith(root, StringComparison.Ordinal) ? root : null, capacity: 100);
        for (var i = 0; i < 1_000_000; i++)
            pending.Add(Path.Combine(root, "extract", $"f{i}.txt"), i % 3 == 0 ? ChangeKind.Delete : ChangeKind.Upsert);
        pending.Add(P("/elsewhere/a.txt"), ChangeKind.Upsert);
        Assert.InRange(pending.Count, 1, 101);
        var due = pending.TakeDue();
        Assert.Contains(new DueChange(root, ChangeKind.Rescan), due);
        Assert.DoesNotContain(due, d => d.Kind != ChangeKind.Rescan && d.Path.StartsWith(root, StringComparison.Ordinal));
        Assert.Contains(new DueChange(P("/elsewhere/a.txt"), ChangeKind.Upsert), due);
    }
}
