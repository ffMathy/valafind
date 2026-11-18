namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Converts audio to the model's 16 kHz with a windowed-sinc low-pass interpolator (Blackman-windowed, 32 taps per side
/// at the output rate when downsampling). Good enough that the encoder sees the same spectrum as from a high-quality
/// resampler. Used for every clip: WAV is decoded by Valafind itself, and Media Foundation hands out the stream's own
/// rate.
/// </summary>
/// <remarks>
/// The filter weight of an input sample depends only on its distance from the output sample's (fractional) position.
/// For rates with a small common divisor that fraction takes few distinct values (160 for 44.1 kHz → 16 kHz, 1 for
/// 48 kHz → 16 kHz), so the weights are computed once per phase instead of once per tap: evaluating sin/cos for each of
/// the ~180 taps of every output sample cost several seconds per 30-second clip.
/// </remarks>
internal static class AudioResampler
{
    private const int HalfTaps = 32;

    /// <summary>Largest number of phases tabulated (≈ 180 taps each); rarer rate pairs compute weights directly.</summary>
    private const int MaxPhases = 4096;

    public static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        if (inputRate == outputRate)
            return input.ToArray();
        if (input.IsEmpty)
            return [];

        var ratio = (double)outputRate / inputRate;
        var outputLength = (int)Math.Floor(input.Length * ratio);
        var output = new float[outputLength];
        // Cut-off at the lower Nyquist frequency; when downsampling the kernel widens accordingly.
        var cutoff = Math.Min(1.0, ratio);
        var halfWidth = HalfTaps / cutoff; // in input samples

        // Output sample i sits at input position i * M / L = q + r / L (M, L: the rates divided by their gcd).
        var gcd = Gcd(inputRate, outputRate);
        var phases = outputRate / gcd;
        var step = inputRate / gcd;
        if (phases > MaxPhases)
        {
            ResampleDirect(input, output, ratio, cutoff, halfWidth);
            return output;
        }

        // Per phase r: taps k (input index offset from q) and their weights w(k - r/L).
        var kernels = new (int First, float[] Weights)[phases];
        for (var r = 0; r < phases; r++)
        {
            var frac = (double)r / phases;
            var first = (int)Math.Ceiling(frac - halfWidth);
            var last = (int)Math.Floor(frac + halfWidth);
            var weights = new float[last - first + 1];
            for (var k = first; k <= last; k++)
            {
                var t = k - frac;
                weights[k - first] = (float)(Sinc(t * cutoff) * Blackman(t / halfWidth));
            }
            kernels[r] = (first, weights);
        }

        for (var i = 0; i < outputLength; i++)
        {
            var position = (long)i * step;
            var q = (int)(position / phases);
            var (first, weights) = kernels[(int)(position % phases)];
            var start = q + first;
            var lo = Math.Max(0, -start);                              // first tap inside the input
            var hi = Math.Min(weights.Length, input.Length - start);   // one past the last
            double sum = 0, weightSum = 0;
            for (var k = lo; k < hi; k++)
            {
                var w = weights[k];
                sum += input[start + k] * w;
                weightSum += w;
            }

            output[i] = weightSum == 0 ? 0f : (float)(sum / weightSum);
        }

        return output;
    }

    /// <summary>The untabulated filter (rate pairs with thousands of phases).</summary>
    private static void ResampleDirect(ReadOnlySpan<float> input, float[] output, double ratio, double cutoff, double halfWidth)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var center = i / ratio;
            var first = (int)Math.Ceiling(center - halfWidth);
            var last = (int)Math.Floor(center + halfWidth);
            double sum = 0, weightSum = 0;
            for (var j = Math.Max(0, first); j <= Math.Min(input.Length - 1, last); j++)
            {
                var t = j - center;
                var w = Sinc(t * cutoff) * Blackman(t / halfWidth);
                sum += input[j] * w;
                weightSum += w;
            }

            output[i] = weightSum == 0 ? 0f : (float)(sum / weightSum);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9)
            return 1.0;
        var px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    /// <summary>Blackman window on [-1, 1].</summary>
    private static double Blackman(double x)
    {
        if (x <= -1 || x >= 1)
            return 0;
        var n = (x + 1) / 2; // 0..1
        return 0.42 - 0.5 * Math.Cos(2 * Math.PI * n) + 0.08 * Math.Cos(4 * Math.PI * n);
    }
}
