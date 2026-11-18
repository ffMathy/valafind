#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Abstractions;
using Valafind.Core.Embedding;
using Valafind.Core.Extraction.Media;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>
/// The GPU (WebGPU) path and its safety net. Needs <c>VALAFIND_TEST_MODEL_DIR</c>; the tests that run inference on the
/// GPU additionally need <c>VALAFIND_TEST_GPU=1</c> (CI machines have no usable GPU).
/// </summary>
public sealed class GpuEmbeddingTests(ModelFixture fixture) : IClassFixture<ModelFixture>, IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("valafind-gpu-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Marker => Path.Combine(_scratch, "gpu-check.pending");

    private EmbeddingGemmaModel Create(EmbeddingDevice device) => new(new EmbeddingOptions
    {
        ModelDirectory = fixture.RequireModel(),
        Dimensions = 768,
        Device = device,
        GpuCrashMarker = Marker,
    });

    private static void RequireGpu() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("VALAFIND_TEST_GPU") == "1", "VALAFIND_TEST_GPU=1 is not set.");

    [Fact]
    public void Cpu_device_never_touches_the_gpu_or_the_marker()
    {
        using var model = Create(EmbeddingDevice.Cpu);
        model.EnsureLoaded();

        Assert.Equal("CPU", model.ActiveDevice);
        Assert.Null(model.GpuUnavailableReason);
        Assert.False(File.Exists(Marker));
    }

    [Fact]
    public void A_marker_left_by_a_crash_keeps_auto_on_the_cpu()
    {
        fixture.RequireModel();
        File.WriteAllText(Marker, EmbeddingGemmaModel.GpuMarkerStamp);
        using var model = Create(EmbeddingDevice.Auto);
        model.EnsureLoaded();

        Assert.Equal("CPU", model.ActiveDevice);
        Assert.Contains(Marker, model.GpuUnavailableReason, StringComparison.Ordinal);
        Assert.True(File.Exists(Marker)); // left for the user (or a newer build) to clear
    }

    [Fact]
    public void A_marker_from_another_build_is_ignored()
    {
        RequireGpu();
        File.WriteAllText(Marker, "0.0.0.0 webgpu 0.0.0.0");
        using var model = Create(EmbeddingDevice.Auto);
        model.EnsureLoaded();

        Assert.StartsWith("GPU", model.ActiveDevice, StringComparison.Ordinal);
        Assert.False(File.Exists(Marker)); // the GPU check passed and cleared it
    }

    [Fact]
    public void Gpu_vectors_match_the_cpu_for_text_pictures_video_and_audio()
    {
        RequireGpu();
        using var gpu = Create(EmbeddingDevice.Gpu);
        gpu.EnsureLoaded();
        Assert.StartsWith("GPU", gpu.ActiveDevice, StringComparison.Ordinal);
        Assert.False(File.Exists(Marker));
        var cpu = fixture.Full;

        var docs = fixture.Reference.Texts.Select(t => new DocumentText("doc.txt", t)).ToList();
        var expected = cpu.EmbedDocuments(docs, Ct);
        var actual = gpu.EmbedDocuments(docs, Ct);
        for (var i = 0; i < docs.Count; i++)
            Assert.True(ModelFixture.Cosine(expected[i], actual[i]) > 0.9999, $"text {i}: {ModelFixture.Cosine(expected[i], actual[i])}");
        Assert.True(ModelFixture.Cosine(cpu.EmbedQuery("red planet", Ct), gpu.EmbedQuery("red planet", Ct)) > 0.9999);

        var decoder = new MediaDecoder();
        var image = decoder.DecodeImageAsync(fixture.Reference.Images[1].Path, 2048, Ct).GetAwaiter().GetResult()!;
        Assert.True(ModelFixture.Cosine(cpu.EmbedImage(image, Ct), gpu.EmbedImage(image, Ct)) > 0.9999);
        Assert.False(File.Exists(Marker));

        var frames = decoder.DecodeVideoAsync(fixture.Reference.Video.Path, 8, 1280, Ct).GetAwaiter().GetResult()!;
        Assert.True(ModelFixture.Cosine(cpu.EmbedVideo(frames.Frames, Ct), gpu.EmbedVideo(frames.Frames, Ct)) > 0.9999);

        var clip = Assert.Single(decoder.DecodeAudioAsync(fixture.Reference.Audio.Path, 1, Ct).GetAwaiter().GetResult()!);
        Assert.True(ModelFixture.Cosine(cpu.EmbedAudio(clip, Ct), gpu.EmbedAudio(clip, Ct)) > 0.9999);
        Assert.False(File.Exists(Marker));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
