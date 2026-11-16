using Valafind.Core.Abstractions;
using Valafind.Core.Storage;

namespace Valafind.Core.Tests.Storage;

/// <summary>A temp directory holding a database file, deleted on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "valafind-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "index.db");
    }

    public string Directory { get; }

    public string Path { get; }

    public SqliteIndexStore OpenStore(string modelId = "test-model", int dims = 8, PathKeyPolicy? policy = null)
    {
        var store = new SqliteIndexStore(Path, policy ?? PathKeyPolicy.CaseSensitive);
        store.Open(modelId, dims);
        return store;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Vectors
{
    public static float[] Random(Random rng, int dims)
    {
        var v = new float[dims];
        for (var i = 0; i < dims; i++)
        {
            // Box-Muller gaussian for isotropic directions.
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            v[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return Normalize(v);
    }

    public static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    /// <summary>Unit vector along axis <paramref name="axis"/>.</summary>
    public static float[] Axis(int dims, int axis, float other = 0f)
    {
        var v = Enumerable.Repeat(other, dims).ToArray();
        v[axis] = 1f;
        return Normalize(v);
    }

    public static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++)
            s += a[i] * b[i];
        return s;
    }
}

internal static class Entries
{
    public static readonly DateTimeOffset BaseTime = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    public static FileEntry File(string path, FileKind kind = FileKind.Text, IndexPhase phase = IndexPhase.All,
        long size = 100, DateTimeOffset? modified = null) =>
        new(path, size, modified ?? BaseTime, kind, phase);

    public static ChunkRecord Chunk(int index, float[] vector, string? text = null) =>
        new(index, index * 100, 100, text ?? $"chunk {index}", vector);
}
