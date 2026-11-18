using Microsoft.Data.Sqlite;
using Valafind.Core.Abstractions;
using Valafind.Core.Indexing;
using Valafind.Core.Platform;
using Valafind.Core.Storage;
using Valafind.Core.Tests.Indexing;
using Xunit;
using static Valafind.Core.Tests.Storage.Entries;
using static Valafind.Core.Tests.TestPaths;

namespace Valafind.Core.Tests.Storage;

/// <summary>Uninstalling gives back every cloud download Valafind still has on record, and leaves the index alone.</summary>
public sealed class CloudDownloadCleanupTests : IDisposable
{
    private const int Dims = 8;
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset At = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private void Record(params string[] paths)
    {
        using var store = _db.OpenStore();
        store.UpsertFile(File(P("/indexed/kept.txt")), [Chunk(0, Vectors.Axis(Dims, 0))]);
        foreach (var path in paths)
            ((ICloudDownloadJournal)store).AddDownload(new CloudDownload(path, 10, IndexPhase.All, new CloudMark(2, 77), At));
    }

    private int Recorded()
    {
        using var store = _db.OpenStore();
        return ((ICloudDownloadJournal)store).LoadDownloads().Count;
    }

    [Fact]
    public void GivesBackEveryRecordedDownloadAsAFinalAttemptAndKeepsTheIndex()
    {
        Record(P("/cloud/a.txt"), P("/cloud/b.txt"));
        var cloud = new FakeCloud { ReleaseBusy = true }; // in use, but a final attempt does not wait for a retry

        var result = CloudDownloadCleanup.ReleaseAll(_db.Path, cloud);

        Assert.Equal(new CloudCleanupResult(2, 0), result);
        Assert.Equal([P("/cloud/a.txt"), P("/cloud/b.txt")], cloud.Released);
        Assert.All(cloud.FinalReleases, Assert.True);
        Assert.Equal(0, Recorded());
        using var store = _db.OpenStore();
        Assert.NotNull(store.GetEntry(P("/indexed/kept.txt")));
    }

    [Fact]
    public void DownloadsThatCannotBeGivenBackStayRecordedForALaterValafind()
    {
        Record(P("/cloud/a.txt"));
        var cloud = new FakeCloud { ReleaseFails = true };

        var result = CloudDownloadCleanup.ReleaseAll(_db.Path, cloud);

        Assert.Equal(new CloudCleanupResult(0, 1), result);
        Assert.Equal(1, Recorded());
    }

    [Fact]
    public void NoDatabaseMeansNothingToDo()
    {
        var result = CloudDownloadCleanup.ReleaseAll(Path.Combine(_db.Directory, "missing.db"), new FakeCloud());

        Assert.Equal(new CloudCleanupResult(0, 0), result);
        Assert.False(System.IO.File.Exists(Path.Combine(_db.Directory, "missing.db")));
    }

    [Fact]
    public void DatabaseFromBeforeCloudDownloadsIsLeftUntouched()
    {
        using (var conn = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            cmd.ExecuteNonQuery();
        }

        var result = CloudDownloadCleanup.ReleaseAll(_db.Path, new FakeCloud());

        Assert.Equal(new CloudCleanupResult(0, 0), result);
        using (var conn = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'cloud_downloads';";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }
}
