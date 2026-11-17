#pragma warning disable CA1707 // xunit test names use underscores for readability

using System.Reflection;
using Valafind.Core.Embedding;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

public sealed class BundledModelTests : IDisposable
{
    // This test assembly embeds Fixtures/BundledModel/** under the same prefix release builds use.
    private static readonly Assembly Bundle = typeof(BundledModelTests).Assembly;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "valafind-bundled-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private string Target => Path.Combine(_root, "models", ModelLocator.ModelFolderName);

    private static string Fixture(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "BundledModel", relative.Replace('/', Path.DirectorySeparatorChar));

    private string Installed(string relative) => Path.Combine(Target, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Lists_embedded_files_with_forward_slash_paths()
    {
        var files = BundledModel.Files(Bundle).Select(f => f.RelativePath).ToArray();
        Assert.Equal(["config.json", "onnx/model_quantized.onnx", "onnx/model_quantized.onnx_data", "tokenizer.json"], files);
        Assert.True(BundledModel.IsBundled(Bundle));
    }

    [Fact]
    public void Assembly_without_model_bundles_nothing()
    {
        var core = typeof(BundledModel).Assembly;
        Assert.False(BundledModel.IsBundled(core));
        Assert.Equal(0, BundledModel.EnsureInstalled(core, Target));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void Unpacks_into_a_valid_model_folder()
    {
        Assert.Equal(4, BundledModel.EnsureInstalled(Bundle, Target));

        foreach (var (relative, _) in BundledModel.Files(Bundle))
            Assert.Equal(File.ReadAllBytes(Fixture(relative)), File.ReadAllBytes(Installed(relative)));
        Assert.True(ModelLocator.IsValid(Target, out var modelFile));
        Assert.Equal("onnx/model_quantized.onnx", modelFile);
        Assert.Empty(Directory.GetFiles(Target, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Second_run_writes_nothing()
    {
        BundledModel.EnsureInstalled(Bundle, Target);
        var stamp = File.GetLastWriteTimeUtc(Installed("tokenizer.json"));

        Assert.Equal(0, BundledModel.EnsureInstalled(Bundle, Target));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Installed("tokenizer.json")));
    }

    [Fact]
    public void Repairs_missing_and_truncated_files_only()
    {
        BundledModel.EnsureInstalled(Bundle, Target);
        File.Delete(Installed("tokenizer.json"));
        File.WriteAllBytes(Installed("onnx/model_quantized.onnx_data"), [1, 2, 3]);

        Assert.Equal(2, BundledModel.EnsureInstalled(Bundle, Target));
        Assert.Equal(File.ReadAllBytes(Fixture("tokenizer.json")), File.ReadAllBytes(Installed("tokenizer.json")));
        Assert.Equal(File.ReadAllBytes(Fixture("onnx/model_quantized.onnx_data")),
            File.ReadAllBytes(Installed("onnx/model_quantized.onnx_data")));
    }

    [Fact]
    public void Honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => BundledModel.EnsureInstalled(Bundle, Target, cts.Token));
    }
}
