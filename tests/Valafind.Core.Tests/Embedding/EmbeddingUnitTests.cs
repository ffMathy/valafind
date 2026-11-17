#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>Tests that do not need the model files.</summary>
public sealed class EmbeddingUnitTests
{
    [Fact]
    public void Query_prompt_matches_model_card()
    {
        Assert.Equal("task: search result | query: Which planet is known as the Red Planet?",
            EmbeddingPrompts.Query("Which planet is known as the Red Planet?"));
    }

    [Theory]
    [InlineData(null, "title: none | text: body")]
    [InlineData("", "title: none | text: body")]
    [InlineData("   ", "title: none | text: body")]
    [InlineData("report.pdf", "title: report.pdf | text: body")]
    [InlineData("  padded.txt \n", "title: padded.txt | text: body")]
    [InlineData("two\nlines\tand\r\ntabs", "title: two lines and tabs | text: body")]
    public void Document_prompt_matches_model_card(string? title, string expected)
    {
        Assert.Equal(expected, EmbeddingPrompts.Document(title, "body"));
    }

    [Fact]
    public void Matryoshka_truncates_and_renormalises()
    {
        float[] v = [3, 4, 12, 0];
        var t = Matryoshka.TruncateAndNormalize(v, 2);
        Assert.Equal(2, t.Length);
        Assert.Equal(0.6f, t[0], 1e-6f);
        Assert.Equal(0.8f, t[1], 1e-6f);

        var full = Matryoshka.TruncateAndNormalize(v, 4);
        Assert.Equal(3f / 13, full[0], 1e-6f);
        Assert.Equal(12f / 13, full[2], 1e-6f);
        Assert.Equal(4f, v[1]); // input untouched
    }

    [Fact]
    public void Matryoshka_handles_zero_vectors_and_bad_sizes()
    {
        var zero = Matryoshka.TruncateAndNormalize(new float[8], 4);
        Assert.All(zero, x => Assert.Equal(0f, x));
        Assert.Throws<ArgumentOutOfRangeException>(() => Matryoshka.TruncateAndNormalize(new float[4], 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Matryoshka.TruncateAndNormalize(new float[4], 0));
    }

    [Theory]
    [InlineData(768)]
    [InlineData(512)]
    [InlineData(256)]
    [InlineData(128)]
    public void Matryoshka_output_is_unit_length(int dims)
    {
        var rng = new Random(dims);
        var v = Enumerable.Range(0, 768).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var t = Matryoshka.TruncateAndNormalize(v, dims);
        Assert.Equal(dims, t.Length);
        Assert.InRange(ModelFixture.Norm(t), 0.99999, 1.00001);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(300)]
    [InlineData(1024)]
    [InlineData(0)]
    public void Options_reject_unsupported_dimensions(int dims)
    {
        var options = new EmbeddingOptions { ModelDirectory = "/nonexistent", Dimensions = dims };
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingGemmaModel(options));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(8193)]
    public void Options_reject_out_of_range_token_budgets(int maxTokens)
    {
        var options = new EmbeddingOptions { ModelDirectory = "/nonexistent", MaxTokens = maxTokens };
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingGemmaModel(options));
    }

    [Fact]
    public void Options_reject_bad_batch_size_and_threads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = "/x", BatchSize = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = "/x", IntraOpThreads = -1 }));
        Assert.Throws<ArgumentException>(() => new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = " " }));
    }

    [Fact]
    public void Options_defaults_match_specification()
    {
        var o = new EmbeddingOptions { ModelDirectory = "/m" };
        Assert.Equal("onnx/model_quantized.onnx", o.ModelFile);
        Assert.Equal(256, o.Dimensions);
        Assert.Equal(512, o.MaxTokens);
        Assert.Equal(2, o.IntraOpThreads);
        Assert.Equal(8, o.BatchSize);
    }

    [Fact]
    public void Reserved_prompt_tokens_leave_room_for_text()
    {
        Assert.True(EmbeddingGemmaModel.MinTokens > EmbeddingGemmaModel.ReservedPromptTokens);
    }
}
