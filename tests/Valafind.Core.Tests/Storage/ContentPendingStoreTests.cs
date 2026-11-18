using Microsoft.Data.Sqlite;
using Valafind.Core.Abstractions;
using Valafind.Core.Storage;
using Xunit;
using static Valafind.Core.Tests.Storage.Entries;
using static Valafind.Core.Tests.TestPaths;

namespace Valafind.Core.Tests.Storage;

/// <summary>Online-only files indexed by name until their content can be read (schema 2).</summary>
public sealed class ContentPendingStoreTests : IDisposable
{
    private const int Dims = 8;
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ContentPendingRoundTripsAndIsClearedByAFullIndex()
    {
        using var store = _db.OpenStore();
        var path = P("/cloud/report.pdf");
        store.UpsertFile(File(path, FileKind.Pdf) with { ContentPending = true }, [Chunk(0, Vectors.Axis(Dims, 0), "")]);
        Assert.True(store.GetEntry(path)!.ContentPending);

        store.UpsertFile(File(path, FileKind.Pdf), [Chunk(0, Vectors.Axis(Dims, 1))]);
        Assert.False(store.GetEntry(path)!.ContentPending);
    }

    [Fact]
    public void SchemaOneDatabaseGainsTheColumnAndKeepsItsFiles()
    {
        using (var store = _db.OpenStore())
            store.UpsertFile(File(P("/f/a")), [Chunk(0, Vectors.Axis(Dims, 0))]);

        // Turn it back into a schema-1 database (no content_pending column).
        using (var conn = new SqliteConnection($"Data Source={_db.Path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE files DROP COLUMN content_pending; UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }

        using (var store = _db.OpenStore())
        {
            var entry = store.GetEntry(P("/f/a"));
            Assert.NotNull(entry);
            Assert.False(entry.ContentPending);
            store.UpsertFile(File(P("/f/b")) with { ContentPending = true }, [Chunk(0, Vectors.Axis(Dims, 1))]);
            Assert.True(store.GetEntry(P("/f/b"))!.ContentPending);
        }
    }
}
