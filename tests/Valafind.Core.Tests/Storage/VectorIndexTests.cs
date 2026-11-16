using Valafind.Core.Abstractions;
using Valafind.Core.Storage;
using Xunit;

namespace Valafind.Core.Tests.Storage;

public sealed class VectorMathTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(256)]
    [InlineData(768)]
    public void Int8_dot_matches_scalar_reference(int n)
    {
        var rng = new Random(n);
        for (var trial = 0; trial < 20; trial++)
        {
            var a = new sbyte[n];
            var b = new sbyte[n];
            for (var i = 0; i < n; i++)
            {
                a[i] = (sbyte)rng.Next(-127, 128);
                b[i] = (sbyte)rng.Next(-127, 128);
            }
            if (trial == 0)
            {
                Array.Fill(a, (sbyte)127);
                Array.Fill(b, (sbyte)-127);
            }
            var expected = 0;
            for (var i = 0; i < n; i++)
                expected += a[i] * b[i];
            Assert.Equal(expected, VectorMath.DotInt8(a, b));
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(37)]
    [InlineData(256)]
    public void Float_dot_matches_scalar_reference(int n)
    {
        var rng = new Random(n);
        var a = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var b = Enumerable.Range(0, n).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var expected = 0.0;
        for (var i = 0; i < n; i++)
            expected += a[i] * b[i];
        Assert.Equal(expected, VectorMath.Dot(a, b), 4);
    }

    [Fact]
    public void Quantize_is_symmetric_and_bounded()
    {
        float[] v = [0.5f, -1f, 0.25f, 0f];
        var codes = new sbyte[4];
        var scale = VectorMath.Quantize(v, codes);
        Assert.Equal(1f / 127f, scale, 6);
        Assert.Equal([64, -127, 32, 0], codes.Select(c => (int)c));

        Assert.Equal(0f, VectorMath.Quantize(new float[4], codes));
        Assert.All(codes, c => Assert.Equal(0, c));
        Assert.Equal(0f, VectorMath.Quantize([float.NaN, 1, 2, 3], codes));
    }

    [Fact]
    public void Blob_round_trips_little_endian()
    {
        float[] v = [1f, -2.5f, 3.25f];
        var blob = VectorMath.ToBlob(v);
        Assert.Equal(12, blob.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, blob.Take(4)); // 1.0f, little-endian
        var back = new float[3];
        Assert.True(VectorMath.TryReadBlob(blob, back));
        Assert.Equal(v, back);
        Assert.False(VectorMath.TryReadBlob(blob, new float[4]));
    }

    [Fact]
    public void Normalize_rejects_zero_and_nan()
    {
        Assert.False(VectorMath.TryNormalize(new float[3]));
        Assert.False(VectorMath.TryNormalize(new[] { float.NaN, 1f }));
        var v = new[] { 3f, 4f };
        Assert.True(VectorMath.TryNormalize(v));
        Assert.Equal([0.6f, 0.8f], v);
    }
}

public sealed class QuantizedVectorIndexTests
{
    [Fact]
    public void Segments_tombstones_and_compaction_preserve_results()
    {
        const int dims = 16;
        var index = new QuantizedVectorIndex(dims, segmentRows: 7); // tiny segments: files straddle boundaries
        var rng = new Random(3);
        var vectors = new Dictionary<long, float[][]>();
        for (long id = 1; id <= 40; id++)
        {
            var count = (int)(id % 4);
            var vs = Enumerable.Range(0, count).Select(_ => Vectors.Random(rng, dims)).ToArray();
            vectors[id] = vs;
            index.SetFile(id, FileKind.Text, 0, vs.SelectMany(v => v).ToArray(), count);
        }
        for (long id = 1; id <= 40; id += 3)
        {
            index.RemoveFile(id);
            vectors.Remove(id);
        }
        Assert.True(index.DeadRows > 0);
        Assert.Equal(vectors.Values.Sum(v => v.Length), index.LiveRows);

        var compacted = index.Compact();
        Assert.Equal(0, compacted.DeadRows);
        Assert.Equal(index.LiveRows, compacted.RowCount);
        Assert.Equal(vectors.Count(kv => kv.Value.Length > 0), compacted.FileCount);

        var q = Vectors.Random(rng, dims);
        var before = index.Search(q, null, long.MinValue, 1000).OrderBy(c => c.FileId).ToList();
        var after = compacted.Search(q, null, long.MinValue, 1000).OrderBy(c => c.FileId).ToList();
        Assert.Equal(before, after);
        Assert.Equal(vectors.Count(kv => kv.Value.Length > 0), after.Count);
        Assert.Equal(after.Count, after.Select(c => c.FileId).Distinct().Count());

        // Per-file max over chunks (approximately equals exact max).
        foreach (var c in after)
        {
            var exact = vectors[c.FileId].Max(v => Vectors.Dot(v, q));
            Assert.InRange(c.ApproximateScore - exact, -0.01f, 0.01f);
        }
    }

    [Fact]
    public void Search_returns_at_most_max_files_with_highest_scores()
    {
        const int dims = 32;
        var index = new QuantizedVectorIndex(dims, segmentRows: 10);
        var rng = new Random(4);
        var all = new List<(long Id, float[] V)>();
        for (long id = 1; id <= 100; id++)
        {
            var v = Vectors.Random(rng, dims);
            all.Add((id, v));
            index.SetFile(id, FileKind.Text, 0, v, 1);
        }
        var q = Vectors.Random(rng, dims);
        var got = index.Search(q, null, long.MinValue, 5);
        Assert.Equal(5, got.Count);
        var top = all.OrderByDescending(x => Vectors.Dot(x.V, q)).Take(5).Select(x => x.Id).ToHashSet();
        Assert.True(got.Count(c => top.Contains(c.FileId)) >= 4);
    }

    [Fact]
    public void Parallel_scan_of_large_index_finds_true_top_files()
    {
        const int dims = 8;
        var rows = QuantizedVectorIndex.ParallelThresholdRows + 5000;
        var index = new QuantizedVectorIndex(dims);
        var rng = new Random(8);
        var all = new float[rows][];
        for (var i = 0; i < rows; i++)
        {
            all[i] = Vectors.Random(rng, dims);
            index.SetFile(i + 1, (FileKind)(i % 3), i, all[i], 1);
        }
        var q = Vectors.Random(rng, dims);
        var kinds = new bool[256];
        kinds[(int)FileKind.Document] = true;

        var got = index.Search(q, kinds, rows / 2, 40).Select(c => c.FileId).ToHashSet();
        var expected = Enumerable.Range(0, rows)
            .Where(i => (FileKind)(i % 3) == FileKind.Document && i > rows / 2)
            .OrderByDescending(i => Vectors.Dot(all[i], q))
            .Take(10)
            .Select(i => (long)(i + 1));
        Assert.Equal(40, got.Count);
        Assert.All(got, id => Assert.True((FileKind)((id - 1) % 3) == FileKind.Document && id - 1 > rows / 2));
        Assert.True(expected.Count(got.Contains) >= 9);
    }
}

public sealed class PathKeyPolicyTests
{
    [Fact]
    public void Directory_range_covers_children_only()
    {
        var (exact, lower, upper) = PathKeyPolicy.CaseSensitive.DirectoryRange("/home/a/docs/");
        Assert.Equal("/home/a/docs", exact);
        Assert.Equal("/home/a/docs/", lower);
        Assert.Equal("/home/a/docs0", upper);

        static bool Under(string key, string lo, string hi) => string.CompareOrdinal(key, lo) >= 0 && string.CompareOrdinal(key, hi) < 0;
        Assert.True(Under("/home/a/docs/x", lower, upper));
        Assert.True(Under("/home/a/docs/z/ü/𝄞", lower, upper));
        Assert.False(Under("/home/a/docs2/x", lower, upper));
        Assert.False(Under("/home/a/docs.txt", lower, upper));
        Assert.False(Under("/home/a/docs-old/x", lower, upper));
    }

    [Fact]
    public void Root_range_covers_everything()
    {
        var (_, lower, upper) = PathKeyPolicy.CaseSensitive.DirectoryRange("/");
        Assert.Equal("/", lower);
        Assert.Equal("0", upper);
    }

    [Fact]
    public void Case_insensitive_keys_are_lowercase_nfc()
    {
        var policy = PathKeyPolicy.CaseInsensitiveSlash;
        Assert.Equal(policy.KeyOf("/A/Ä.txt"), policy.KeyOf("/a/Ä.TXT"));
        Assert.Equal("/a/b", PathKeyPolicy.CaseSensitive.KeyForPath("/a/./c/../b"));
    }
}
