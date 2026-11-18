using Valafind.Core.Indexing;
using Valafind.Core.Storage;
using Xunit;
using static Valafind.Core.Tests.TestPaths;

namespace Valafind.Core.Tests.Storage;

public sealed class SqliteIndexStoreReviewFixTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static void Add(SqliteIndexStore store, string path) =>
        store.UpsertFile(Entries.File(path), [Entries.Chunk(0, Vectors.Axis(8, 0))]);

    [Fact]
    public void Indexed_paths_are_listed_page_by_page()
    {
        using var store = _db.OpenStore();
        var paths = Enumerable.Range(0, 25).Select(i => P($"/home/a/f{i:D2}.txt")).ToList();
        foreach (var path in paths)
            Add(store, path);

        Assert.IsAssignableFrom<IIndexedPathSource>(store);
        var source = store;
        var listed = new List<string>();
        string? after = null;
        var pages = 0;
        while (true)
        {
            var page = source.GetIndexedPaths(null, after, 10);
            if (page.Count == 0)
                break;
            pages++;
            listed.AddRange(page.Select(p => p.Path));
            after = page[^1].Key;
            // Removing what was just listed must not disturb the cursor.
            foreach (var entry in page.Take(3))
                store.RemoveFile(entry.Path);
        }
        Assert.Equal(3, pages);
        Assert.Equal(paths, listed);
    }

    [Fact]
    public void Indexed_paths_can_be_limited_to_a_directory()
    {
        using var store = _db.OpenStore();
        foreach (var path in new[] { "/home/a/docs", "/home/a/docs/x.txt", "/home/a/docs/sub/y.txt", "/home/a/docs2/z.txt", "/home/a/doc.txt" })
            Add(store, P(path));
        var listed = store.GetIndexedPaths(P("/home/a/docs"), null, 100).Select(p => p.Path).ToList();
        Assert.Equal([P("/home/a/docs"), P("/home/a/docs/sub/y.txt"), P("/home/a/docs/x.txt")], listed.Order(StringComparer.Ordinal));
        // Continuing after the directory's own key (case-sensitive policy: key = path) lists what is below it.
        Assert.Equal(2, store.GetIndexedPaths(P("/home/a/docs/"), P("/home/a/docs"), 100).Count);
    }

    [Fact]
    public void Entry_lookup_returns_the_stored_spelling()
    {
        using var store = _db.OpenStore(policy: PathKeyPolicy.CaseInsensitiveNfc);
        Assert.IsAssignableFrom<IIndexedEntrySource>(store);
        Add(store, P("/home/a/report.docx"));
        var entry = store.GetEntry(P("/home/a/Report.docx"));
        Assert.NotNull(entry);
        Assert.Equal(P("/home/a/report.docx"), entry.Path);
        Assert.Equal(100, entry.Size);
        Assert.Equal(Entries.BaseTime, entry.ModifiedUtc);
        Assert.Null(store.GetEntry(P("/home/a/other.docx")));

        // Re-indexing under the new spelling replaces the stored one (a single row).
        Add(store, P("/home/a/Report.docx"));
        Assert.Equal(P("/home/a/Report.docx"), store.GetEntry(P("/home/a/report.docx"))!.Path);
        Assert.Equal(1, store.GetStats().Files);
    }

    [Fact]
    public void Batch_entry_lookup_answers_in_input_order()
    {
        using var store = _db.OpenStore(policy: PathKeyPolicy.CaseInsensitiveNfc);
        Add(store, P("/home/a/report.docx"));
        Add(store, P("/home/a/notes.txt"));
        IIndexedEntrySource source = store;

        // More paths than one query takes, with misses, a different spelling and a repeat.
        var paths = Enumerable.Range(0, 1200).Select(i => P($"/home/a/missing{i}.txt")).ToList();
        paths.Insert(3, P("/home/a/Report.docx"));
        paths.Insert(700, P("/home/a/notes.txt"));
        paths.Add(P("/home/a/notes.txt"));
        var entries = source.GetEntries(paths);

        Assert.Equal(paths.Count, entries.Count);
        Assert.Equal(P("/home/a/report.docx"), entries[3]!.Path); // the stored spelling
        Assert.Equal(P("/home/a/notes.txt"), entries[700]!.Path);
        Assert.Equal(P("/home/a/notes.txt"), entries[^1]!.Path);
        Assert.Equal(100, entries[3]!.Size);
        Assert.Equal(Entries.BaseTime, entries[3]!.ModifiedUtc);
        Assert.Equal(3, entries.Count(e => e is not null));
        Assert.Empty(source.GetEntries([]));
    }

    [Fact]
    public void Database_directory_and_files_are_private_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // per-user profile ACLs

        var directory = Path.Combine(_db.Directory, "data", "valafind");
        var path = Path.Combine(directory, "index.db");
        using (var store = new SqliteIndexStore(path, PathKeyPolicy.CaseSensitive))
        {
            store.Open("m", 8);
            Add(store, P("/home/a/x.txt")); // WAL in use: -wal and -shm exist
            AssertPrivate(directory);
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                Assert.True(File.Exists(file), file);
                AssertPrivate(file);
            }
        }

        // A database created world-readable by an older version is tightened on open.
        File.SetUnixFileMode(path, (UnixFileMode)0b110_100_100);
        File.SetUnixFileMode(directory, (UnixFileMode)0b111_101_101);
        using (var store = new SqliteIndexStore(path, PathKeyPolicy.CaseSensitive))
        {
            store.Open("m", 8);
            AssertPrivate(directory);
            AssertPrivate(path);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void AssertPrivate(string path)
    {
        const UnixFileMode groupOther = (UnixFileMode)0b000_111_111;
        var mode = File.GetUnixFileMode(path);
        Assert.True((mode & groupOther) == 0, $"{path} has mode {Convert.ToString((int)mode, 8)}");
        Assert.True((mode & UnixFileMode.UserRead) != 0 && (mode & UnixFileMode.UserWrite) != 0, $"{path} not owner-accessible");
    }
}
