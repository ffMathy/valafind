#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Abstractions;
using Valafind.Core.Embedding;
using Valafind.Core.Embedding.Media;
using Valafind.Core.Extraction.Media;
using Xunit;

namespace Valafind.Core.Tests.Embedding;

/// <summary>
/// Pictures, audio and video against the Python reference (tools/make-embedding-reference.py). Preprocessing checks run
/// everywhere; embedding checks need the model (<c>VALAFIND_TEST_MODEL_DIR</c>).
/// </summary>
public sealed class MediaEmbeddingTests(ModelFixture fixture) : IClassFixture<ModelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DecodedImage Decode(MediaReference reference) =>
        SkiaImageDecoder.Decode(reference.Path, 4096, CancellationToken.None) ?? throw new InvalidOperationException("Could not decode " + reference.File);

    // ------------------------------------------------------------------ preprocessing (no model)

    [Theory]
    [InlineData(480, 640, 70, 336, 432)]
    [InlineData(480, 640, 140, 480, 624)]
    [InlineData(480, 640, 280, 672, 912)]
    [InlineData(108, 192, 70, 288, 528)]
    [InlineData(4000, 10, 280, 13440, 48)] // very thin: one 48-pixel side, the other capped
    public void Target_size_matches_the_reference_processor(int height, int width, int softTokens, int expectedHeight, int expectedWidth)
    {
        var (h, w) = VisionPreprocessor.TargetSize(height, width, softTokens * 9);
        Assert.Equal((expectedHeight, expectedWidth), (h, w));
        Assert.Equal(0, h % 48);
        Assert.Equal(0, w % 48);
        Assert.True(h / 16 * (w / 16) <= softTokens * 9);
    }

    [Fact]
    public void Pictures_decode_and_patchify_like_the_reference()
    {
        foreach (var reference in fixture.Reference.Images)
        {
            var image = Decode(reference);
            Assert.Equal((reference.Width, reference.Height), (image.Width, image.Height));
            var patches = VisionPreprocessor.Preprocess(image, 140);
            Assert.Equal(reference.Patches, patches.Patches);
            // Bicubic resampling matches the reference to within a couple of 8-bit levels.
            for (var i = 0; i < reference.Head.Length; i++)
                Assert.True(Math.Abs(reference.Head[i] - patches.PixelValues[i]) <= 2.5 / 255, $"{reference.File}[{i}]: {reference.Head[i]} vs {patches.PixelValues[i]}");
            var sum = patches.PixelValues.Sum(v => (double)v);
            Assert.True(Math.Abs(sum - reference.Sum) <= Math.Abs(reference.Sum) * 0.002, $"{reference.File}: sum {sum} vs {reference.Sum}");
        }
    }

    [Fact]
    public void Exif_orientation_is_applied()
    {
        // rotated.jpg is stored 300×200 with orientation 6 (turn 90° clockwise to view): upright it is 200×300, and the
        // blue bar stored on the left ends up at the top.
        var image = SkiaImageDecoder.Decode(fixture.Reference.Images.Single(i => i.File == "rotated.jpg").Path, 4096, Ct)!;
        Assert.Equal((200, 300), (image.Width, image.Height));
        var top = Pixel(image, 100, 60);
        var bottom = Pixel(image, 100, 260);
        Assert.True(top.B > 150 && top.R < 80, $"top {top}");
        Assert.True(bottom is { R: > 200, G: > 200, B: > 200 }, $"bottom {bottom}");
    }

    private static (byte R, byte G, byte B) Pixel(DecodedImage image, int x, int y)
    {
        var o = (y * image.Width + x) * 3;
        return (image.Rgb[o], image.Rgb[o + 1], image.Rgb[o + 2]);
    }

    [Fact]
    public void Bicubic_resampler_keeps_flat_colour_and_size()
    {
        var rgb = new byte[37 * 23 * 3];
        for (var i = 0; i < rgb.Length; i += 3)
            (rgb[i], rgb[i + 1], rgb[i + 2]) = ((byte)10, (byte)200, (byte)90);
        var up = BicubicResampler.Resize(rgb, 37, 23, 101, 64);
        var down = BicubicResampler.Resize(rgb, 37, 23, 5, 3);
        Assert.Equal(101 * 64 * 3, up.Length);
        Assert.Equal(5 * 3 * 3, down.Length);
        Assert.All(up.Chunk(3), p => Assert.Equal([10, 200, 90], p));
        Assert.All(down.Chunk(3), p => Assert.Equal([10, 200, 90], p));
    }

    [Fact]
    public void Audio_features_match_the_reference_extractor()
    {
        var reference = fixture.Reference.Audio;
        var info = WavReader.Probe(reference.Path)!;
        Assert.Equal(16_000, info.SampleRate);
        var samples = WavReader.Read(reference.Path, info, TimeSpan.Zero, info.Duration, Ct);
        Assert.Equal(reference.Samples, samples.Length);

        var features = AudioFeatureExtractor.Extract(samples);
        Assert.Equal(reference.Frames, features.Frames);
        Assert.Equal(reference.ValidFrames, features.Mask.Count(m => m));
        for (var i = 0; i < reference.Head.Length; i++)
            Assert.True(Math.Abs(reference.Head[i] - features.Features[i]) < 1e-3, $"[{i}]: {reference.Head[i]} vs {features.Features[i]}");
        var sum = features.Features.Sum(v => (double)v);
        Assert.True(Math.Abs(sum - reference.Sum) <= Math.Abs(reference.Sum) * 1e-4, $"sum {sum} vs {reference.Sum}");
    }

    [Fact]
    public void Audio_features_are_capped_at_thirty_seconds_and_handle_silence()
    {
        var features = AudioFeatureExtractor.Extract(new float[40 * 16_000]);
        Assert.Equal((480_000 + 160 - 321) / 160 + 1, features.Frames);
        Assert.All(features.Features, v => Assert.Equal((float)Math.Log(1e-3), v, 1e-4f));
        Assert.Equal(0, AudioFeatureExtractor.Extract([]).Mask.Count(m => m));
    }

    [Fact]
    public void Fft_matches_a_direct_dft()
    {
        var random = new Random(3);
        var re = Enumerable.Range(0, 64).Select(_ => random.NextDouble() - 0.5).ToArray();
        var im = new double[64];
        var copy = (double[])re.Clone();
        AudioFeatureExtractor.Fft.Forward(re, im);
        for (var k = 0; k < 64; k++)
        {
            double sr = 0, si = 0;
            for (var n = 0; n < 64; n++)
            {
                sr += copy[n] * Math.Cos(-2 * Math.PI * k * n / 64);
                si += copy[n] * Math.Sin(-2 * Math.PI * k * n / 64);
            }

            Assert.Equal(sr, re[k], 1e-9);
            Assert.Equal(si, im[k], 1e-9);
        }
    }

    [Theory]
    [InlineData(10, 8, new[] { 0, 1, 2, 3, 5, 6, 7, 9 })]
    [InlineData(4, 8, new[] { 0, 1, 2, 3 })]
    [InlineData(100, 1, new[] { 0 })]
    public void Video_frames_are_thinned_out_like_numpy_linspace(int count, int max, int[] expected) =>
        Assert.Equal(expected, EmbeddingGemmaModel.SelectFrames(count, max));

    // ------------------------------------------------------------------ embeddings (model)

    private EmbeddingGemmaModel Model()
    {
        fixture.RequireModel();
        var model = fixture.Full;
        Assert.Equal(MediaCapabilities.Image | MediaCapabilities.Audio | MediaCapabilities.Video, model.MediaCapabilities);
        return model;
    }

    [Fact]
    public void Picture_embeddings_match_the_reference()
    {
        var model = Model();
        foreach (var reference in fixture.Reference.Images)
        {
            var vector = model.EmbedImage(Decode(reference), Ct);
            Assert.Equal(768, vector.Length);
            Assert.Equal(1.0, ModelFixture.Norm(vector), 4);
            var cosine = ModelFixture.Cosine(vector, reference.Embedding);
            Assert.True(cosine > 0.995, $"{reference.File}: cosine {cosine}");
        }
    }

    [Fact]
    public void Audio_embedding_matches_the_reference()
    {
        var model = Model();
        var reference = fixture.Reference.Audio;
        var info = WavReader.Probe(reference.Path)!;
        var samples = WavReader.Read(reference.Path, info, TimeSpan.Zero, info.Duration, Ct);
        var vector = model.EmbedAudio(new AudioClip(samples, TimeSpan.Zero), Ct);
        var cosine = ModelFixture.Cosine(vector, reference.Embedding);
        Assert.True(cosine > 0.999, $"cosine {cosine}");
    }

    [Fact]
    public async Task Video_embedding_from_the_platform_decoder_matches_the_reference()
    {
        var model = Model();
        var decoder = new MediaDecoder();
        Assert.SkipWhen(decoder.BackendNames.Count == 0, "No audio/video decoder on this machine.");
        var frames = await decoder.DecodeVideoAsync(fixture.Reference.Video.Path, model.MaxVideoFrames, 1280, Ct);
        Assert.NotNull(frames);
        Assert.Equal(fixture.Reference.Video.Frames, frames.Frames.Count);
        var vector = model.EmbedVideo(frames.Frames, Ct);
        var cosine = ModelFixture.Cosine(vector, fixture.Reference.Video.Embedding);
        // Different H.264 decoders differ by a level here and there; the embedding barely moves.
        Assert.True(cosine > 0.98, $"cosine {cosine} ({string.Join(", ", decoder.BackendNames)})");
    }

    [Fact]
    public async Task Text_queries_find_the_matching_media()
    {
        var model = Model();
        var decoder = new MediaDecoder();
        var vectors = new List<(string Name, float[] Vector)>
        {
            ("red-circle", model.EmbedImage(Decode(fixture.Reference.Images[0]), Ct)),
            ("invoice", model.EmbedImage(Decode(fixture.Reference.Images[1]), Ct)),
        };
        var clips = await decoder.DecodeAudioAsync(fixture.Reference.Audio.Path, 4, Ct);
        Assert.NotNull(clips);
        vectors.Add(("speech", model.EmbedAudio(Assert.Single(clips), Ct)));

        string Best(string query)
        {
            var q = model.EmbedQuery(query, Ct);
            return vectors.OrderByDescending(v => ModelFixture.Cosine(q, v.Vector)).First().Name;
        }

        Assert.Equal("red-circle", Best("a red circle"));
        Assert.Equal("invoice", Best("invoice"));
        Assert.Equal("speech", Best("weather forecast"));
    }

    [Fact]
    public void Media_resources_are_released_and_reloaded()
    {
        var model = Model();
        var image = Decode(fixture.Reference.Images[0]);
        var first = model.EmbedImage(image, Ct);
        Assert.True(model.HasMediaSessions);
        model.ReleaseMediaResources();
        Assert.False(model.HasMediaSessions);
        Assert.Equal(first, model.EmbedImage(image, Ct));
    }

    [Fact]
    public async Task Query_preempts_a_running_picture_embedding()
    {
        var model = Model();
        var image = Decode(fixture.Reference.Images[0]);
        var expected = model.EmbedImage(image, Ct);
        var background = Task.Run(() => model.EmbedImage(image, Ct), Ct);
        // Fire queries while the picture runs: they go first and the picture's run is redone, with the same result.
        for (var i = 0; i < 5 && !background.IsCompleted; i++)
        {
            await Task.Delay(50, Ct);
            Assert.Equal(768, model.EmbedQuery("a red circle", Ct).Length);
        }

        Assert.Equal(expected, await background);
    }
}
