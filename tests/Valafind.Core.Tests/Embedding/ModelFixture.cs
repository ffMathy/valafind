#pragma warning disable CA1707 // xunit test names use underscores for readability

using System.Text.Json;
using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>Reference data produced by tools/make-embedding-reference.py (HF tokenizers + python onnxruntime).</summary>
public sealed class ReferenceData
{
    public required string[] Texts { get; init; }
    public required int[][] TokenIds { get; init; }
    public required float[][] Embeddings { get; init; }
    public required double[] ReadmeSimilarities { get; init; }

    public static ReferenceData Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "embeddinggemma-reference.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        return new ReferenceData
        {
            Texts = root.GetProperty("texts").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            TokenIds = root.GetProperty("token_ids").EnumerateArray()
                .Select(e => e.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray(),
            Embeddings = root.GetProperty("embeddings").EnumerateArray()
                .Select(e => e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray()).ToArray(),
            ReadmeSimilarities = root.GetProperty("readme_similarities").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
        };
    }
}

/// <summary>
/// Loads the real EmbeddingGemma model once per test class when <c>VALAFIND_TEST_MODEL_DIR</c> is set.
/// Tests call <see cref="RequireModel"/> which skips when the model is not available (e.g. on CI).
/// </summary>
public sealed class ModelFixture : IDisposable
{
    public const string EnvironmentVariable = "VALAFIND_TEST_MODEL_DIR";

    private readonly Lazy<EmbeddingGemmaModel> _full;
    private readonly Lazy<EmbeddingGemmaModel> _small;

    public ModelFixture()
    {
        ModelDirectory = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Reference = ReferenceData.Load();
        _full = new Lazy<EmbeddingGemmaModel>(() => Create(768, 512, 8));
        _small = new Lazy<EmbeddingGemmaModel>(() => Create(256, 80, 3));
    }

    public string? ModelDirectory { get; }

    public ReferenceData Reference { get; }

    /// <summary>768-d model, default token budget, batch size 8.</summary>
    public EmbeddingGemmaModel Full => _full.Value;

    /// <summary>256-d model with an 80-token budget and batch size 3 (exercises truncation and multi-batch paths).</summary>
    public EmbeddingGemmaModel Small => _small.Value;

    public string RequireModel()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ModelDirectory), $"{EnvironmentVariable} is not set; skipping tests that need the EmbeddingGemma model.");
        Assert.SkipUnless(ModelLocator.IsValid(ModelDirectory!, out _), $"{EnvironmentVariable}='{ModelDirectory}' is not a valid model directory.");
        return ModelDirectory!;
    }

    private EmbeddingGemmaModel Create(int dims, int maxTokens, int batch)
    {
        ModelLocator.IsValid(ModelDirectory!, out var file);
        return new EmbeddingGemmaModel(new EmbeddingOptions
        {
            ModelDirectory = ModelDirectory!,
            ModelFile = file,
            Dimensions = dims,
            MaxTokens = maxTokens,
            BatchSize = batch,
            IntraOpThreads = 2,
        });
    }

    public void Dispose()
    {
        if (_full.IsValueCreated)
            _full.Value.Dispose();
        if (_small.IsValueCreated)
            _small.Value.Dispose();
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Assert.Equal(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return dot / Math.Sqrt(na * nb);
    }

    public static double Norm(ReadOnlySpan<float> a)
    {
        double s = 0;
        foreach (var x in a)
            s += (double)x * x;
        return Math.Sqrt(s);
    }
}
