#pragma warning disable CA1707 // xunit test names use underscores for readability

using System.Diagnostics;
using Valafind.Core.Extraction.Media;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

public sealed class AudioResamplerTests
{
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(22050)]
    [InlineData(11025)]
    [InlineData(8000)]   // upsampling
    [InlineData(32000)]
    [InlineData(47999)]  // thousands of phases: the direct path
    public void Matches_the_per_tap_windowed_sinc_filter(int inputRate)
    {
        var random = new Random(inputRate);
        var input = new float[inputRate / 2 + 37]; // half a second plus a ragged tail
        for (var i = 0; i < input.Length; i++)
            input[i] = (float)(Math.Sin(i * 2 * Math.PI * 440 / inputRate) * 0.5 + (random.NextDouble() - 0.5) * 0.2);

        var expected = Reference(input, inputRate, 16000);
        var actual = AudioResampler.Resample(input, inputRate, 16000);

        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-5, $"[{i}] {expected[i]} vs {actual[i]}");
    }

    [Fact]
    public void Resamples_a_30_second_clip_quickly()
    {
        var input = new float[44100 * 30];
        for (var i = 0; i < input.Length; i++)
            input[i] = (float)Math.Sin(i * 0.01);
        AudioResampler.Resample(input.AsSpan(0, 44100), 44100, 16000); // JIT

        var sw = Stopwatch.StartNew();
        var output = AudioResampler.Resample(input, 44100, 16000);
        sw.Stop();

        Assert.Equal(16000 * 30, output.Length);
        // The per-tap version took ~4 s on a laptop; the tabulated one well under a second even on a slow CI box.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Same_rate_and_empty_input_are_passed_through()
    {
        Assert.Equal([1f, 2f], AudioResampler.Resample([1f, 2f], 16000, 16000));
        Assert.Empty(AudioResampler.Resample([], 44100, 16000));
    }

    /// <summary>The original implementation: weights evaluated for every tap of every output sample.</summary>
    private static float[] Reference(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        var ratio = (double)outputRate / inputRate;
        var output = new float[(int)Math.Floor(input.Length * ratio)];
        var cutoff = Math.Min(1.0, ratio);
        var halfWidth = 32 / cutoff;
        for (var i = 0; i < output.Length; i++)
        {
            var center = i / ratio;
            var first = (int)Math.Ceiling(center - halfWidth);
            var last = (int)Math.Floor(center + halfWidth);
            double sum = 0, weightSum = 0;
            for (var j = Math.Max(0, first); j <= Math.Min(input.Length - 1, last); j++)
            {
                var t = j - center;
                var x = t / halfWidth;
                var sinc = Math.Abs(t * cutoff) < 1e-9 ? 1.0 : Math.Sin(Math.PI * t * cutoff) / (Math.PI * t * cutoff);
                var n = (x + 1) / 2;
                var blackman = x <= -1 || x >= 1 ? 0 : 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n);
                var w = sinc * blackman;
                sum += input[j] * w;
                weightSum += w;
            }
            output[i] = weightSum == 0 ? 0f : (float)(sum / weightSum);
        }
        return output;
    }
}
