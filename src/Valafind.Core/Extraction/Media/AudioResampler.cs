namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Converts audio to the model's 16 kHz with a windowed-sinc low-pass interpolator (Blackman-windowed, 32 taps per side
/// at the output rate when downsampling). Good enough that the encoder sees the same spectrum as from a high-quality
/// resampler; used for formats Valafind decodes itself (WAV), the platform decoders resample on their own.
/// </summary>
internal static class AudioResampler
{
    private const int HalfTaps = 32;

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
        for (var i = 0; i < outputLength; i++)
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

        return output;
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
