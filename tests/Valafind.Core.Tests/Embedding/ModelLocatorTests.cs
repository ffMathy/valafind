#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

public sealed class ModelLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "valafind-locator-" + Guid.NewGuid().ToString("N"));

    public ModelLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string dir, string relative, int bytes = 16)
    {
        var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Fact]
    public void Missing_or_empty_directory_is_invalid()
    {
        Assert.False(ModelLocator.IsValid(Path.Combine(_root, "nope"), out var f1));
        Assert.Equal("", f1);
        Assert.False(ModelLocator.IsValid(Dir("empty"), out _));
        Assert.False(ModelLocator.IsValid("", out _));
    }

    [Fact]
    public void Tokenizer_without_model_is_invalid_and_model_without_tokenizer_is_invalid()
    {
        var a = Dir("tok-only");
        Touch(a, "tokenizer.json");
        Assert.False(ModelLocator.IsValid(a, out _));

        var b = Dir("model-only");
        Touch(b, "onnx/model_quantized.onnx");
        Touch(b, "onnx/model_quantized.onnx_data");
        Assert.False(ModelLocator.IsValid(b, out _));
    }

    [Fact]
    public void Quantized_model_with_external_data_is_valid()
    {
        var d = Dir("q8");
        Touch(d, "tokenizer.json");
        Touch(d, "onnx/model_quantized.onnx");
        Touch(d, "onnx/model_quantized.onnx_data");
        Assert.True(ModelLocator.IsValid(d, out var file));
        Assert.Equal("onnx/model_quantized.onnx", file);
    }

    [Fact]
    public void Small_graph_without_its_external_weights_is_rejected()
    {
        var d = Dir("no-data");
        Touch(d, "tokenizer.model");
        Touch(d, "onnx/model_quantized.onnx");
        Assert.False(ModelLocator.IsValid(d, out _));
    }

    [Fact]
    public void Preference_order_is_quantized_then_fp32_then_q4_and_never_fp16()
    {
        var d = Dir("prefs");
        Touch(d, "tokenizer.model");
        Touch(d, "onnx/model_fp16.onnx");
        Touch(d, "onnx/model_fp16.onnx_data");
        Touch(d, "onnx/model_q4f16.onnx");
        Touch(d, "onnx/model_q4f16.onnx_data");
        Assert.False(ModelLocator.IsValid(d, out _));

        Touch(d, "onnx/model_q4.onnx");
        Touch(d, "onnx/model_q4.onnx_data");
        Assert.True(ModelLocator.IsValid(d, out var f));
        Assert.Equal("onnx/model_q4.onnx", f);

        Touch(d, "onnx/model.onnx");
        Touch(d, "onnx/model.onnx_data");
        Assert.True(ModelLocator.IsValid(d, out f));
        Assert.Equal("onnx/model.onnx", f);

        Touch(d, "onnx/model_quantized.onnx");
        Touch(d, "onnx/model_quantized.onnx_data");
        Assert.True(ModelLocator.IsValid(d, out f));
        Assert.Equal("onnx/model_quantized.onnx", f);
    }

    [Fact]
    public void Root_level_onnx_file_is_accepted()
    {
        var d = Dir("flat");
        Touch(d, "tokenizer.json");
        Touch(d, "model.onnx");
        Touch(d, "model.onnx_data");
        Assert.True(ModelLocator.IsValid(d, out var f));
        Assert.Equal("model.onnx", f);
    }

    [Fact]
    public void Find_prefers_the_configured_directory()
    {
        var d = Dir("configured");
        Touch(d, "tokenizer.json");
        Touch(d, "onnx/model_quantized.onnx");
        Touch(d, "onnx/model_quantized.onnx_data");
        Assert.Equal(d, ModelLocator.Find(d));
    }

    [Fact]
    public void Candidates_are_probed_in_documented_order()
    {
        var candidates = ModelLocator.Candidates("/configured/dir").ToList();
        Assert.Equal("/configured/dir", candidates[0]);
        Assert.Contains(candidates, c => c.EndsWith(Path.Combine("models", ModelLocator.ModelFolderName), StringComparison.Ordinal));
        Assert.Contains(Path.Combine(AppContext.BaseDirectory, "models", ModelLocator.ModelFolderName), candidates);
        Assert.Equal(Path.Combine(Valafind.Core.Configuration.AppPaths.ModelsDirectory, ModelLocator.ModelFolderName),
            candidates[candidates.IndexOf(Path.Combine(AppContext.BaseDirectory, "models", ModelLocator.ModelFolderName)) - 1]);
    }

    [Fact]
    public void Model_constructor_falls_back_from_default_file_and_rejects_fp16()
    {
        var d = Dir("ctor");
        Touch(d, "tokenizer.json");
        Touch(d, "onnx/model.onnx");
        Touch(d, "onnx/model.onnx_data");
        Touch(d, "onnx/model_fp16.onnx");
        Touch(d, "onnx/model_fp16.onnx_data");

        using (var model = new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = d, Dimensions = 128 }))
        {
            Assert.Equal("embeddinggemma-300m/model/128", model.ModelId);
            Assert.Equal(128, model.Dimensions);
        }

        Assert.Throws<NotSupportedException>(() =>
            new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = d, ModelFile = "onnx/model_fp16.onnx" }));
        Assert.Throws<FileNotFoundException>(() =>
            new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = d, ModelFile = "onnx/model_q4.onnx" }));
        Assert.Throws<FileNotFoundException>(() =>
            new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = Dir("ctor-empty") }));
    }

    [Fact]
    public void Model_constructor_requires_a_tokenizer()
    {
        var d = Dir("ctor-no-tok");
        Touch(d, "onnx/model_quantized.onnx");
        Touch(d, "onnx/model_quantized.onnx_data");
        Assert.Throws<FileNotFoundException>(() => new EmbeddingGemmaModel(new EmbeddingOptions { ModelDirectory = d }));
    }
}
