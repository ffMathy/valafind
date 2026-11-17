using System.Numerics;

namespace Valafind.Core.Embedding.Media;

/// <summary>Log-mel input of the audio encoder.</summary>
/// <param name="Features">[frames × 128] log-mel values (zero where <paramref name="Mask"/> is false).</param>
/// <param name="Mask">Per frame: true when its whole analysis window is real audio (not padding).</param>
internal sealed record AudioFeatures(float[] Features, bool[] Mask, int Frames);

/// <summary>
/// Port of the Gemma 4 audio feature extractor (EmbeddingGemma 2): 16 kHz mono, padded with zeros to a multiple of 128
/// samples, at most 30 s; semicausal padding (160 zeros in front); 20 ms frames (320 samples, periodic Hann window) every
/// 10 ms; |rfft(512)|; 128 HTK mel filters 0–8 kHz (no area normalisation); log(mel + 0.001). No pre-emphasis, dither or
/// per-bin normalisation (all off in the model's configuration).
/// </summary>
internal static class AudioFeatureExtractor
{
    public const int SampleRate = 16_000;
    public const int MelBins = 128;
    public const int MaxSamples = 480_000;
    private const int FrameLength = 320;
    private const int HopLength = 160;
    private const int FftLength = 512;
    private const int FrequencyBins = FftLength / 2 + 1;
    private const int PadToMultipleOf = 128;
    private const double MelFloor = 1e-3;

    private static readonly float[] s_window = CreateWindow();
    private static readonly double[] s_melFilters = CreateMelFilters(); // [FrequencyBins × MelBins]

    public static AudioFeatures Extract(ReadOnlySpan<float> samples)
    {
        if (samples.Length > MaxSamples)
            samples = samples[..MaxSamples];
        var length = samples.Length;
        var padded = (length + PadToMultipleOf - 1) / PadToMultipleOf * PadToMultipleOf;

        // Semicausal padding: FrameLength / 2 zeros in front, so the first frame is centred at t = 0.
        const int padLeft = FrameLength / 2;
        var waveform = new double[padLeft + padded];
        for (var i = 0; i < length; i++)
            waveform[padLeft + i] = samples[i];
        var realEnd = padLeft + length; // samples at or after this index are padding

        const int unfoldSize = FrameLength + 1;
        var frames = waveform.Length >= unfoldSize ? (waveform.Length - unfoldSize) / HopLength + 1 : 0;
        var features = new float[frames * MelBins];
        var mask = new bool[frames];
        var re = new double[FftLength];
        var im = new double[FftLength];
        var magnitude = new double[FrequencyBins];
        for (var f = 0; f < frames; f++)
        {
            var start = f * HopLength;
            // The extractor's mask: valid when the last sample of the unfolded window is real audio.
            var lastSample = start + unfoldSize - 1;
            mask[f] = lastSample >= padLeft && lastSample < realEnd;

            Array.Clear(re);
            Array.Clear(im);
            for (var i = 0; i < FrameLength; i++)
                re[i] = (float)waveform[start + i] * s_window[i]; // float32 frames × float32 window, as in numpy
            Fft.Forward(re, im);
            for (var k = 0; k < FrequencyBins; k++)
                magnitude[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);

            if (!mask[f])
                continue; // the extractor multiplies padded frames by 0
            var row = f * MelBins;
            for (var m = 0; m < MelBins; m++)
            {
                var sum = 0.0;
                for (var k = 0; k < FrequencyBins; k++)
                    sum += magnitude[k] * s_melFilters[k * MelBins + m];
                features[row + m] = (float)Math.Log(sum + MelFloor);
            }
        }

        return new AudioFeatures(features, mask, frames);
    }

    /// <summary>Periodic Hann window of FrameLength samples (numpy hanning(N + 1)[:-1]).</summary>
    private static float[] CreateWindow()
    {
        var window = new float[FrameLength];
        for (var n = 0; n < FrameLength; n++)
            window[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FrameLength));
        return window;
    }

    private static double HertzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);

    private static double MelToHertz(double mel) => 700.0 * (Math.Pow(10, mel / 2595.0) - 1.0);

    /// <summary>transformers.audio_utils.mel_filter_bank(257, 128, 0, 8000, 16000, norm=None, mel_scale="htk").</summary>
    private static double[] CreateMelFilters()
    {
        var melMin = HertzToMel(0);
        var melMax = HertzToMel(8000);
        var filterFreqs = new double[MelBins + 2];
        for (var i = 0; i < filterFreqs.Length; i++)
            filterFreqs[i] = MelToHertz(melMin + (melMax - melMin) * i / (MelBins + 1));
        var filters = new double[FrequencyBins * MelBins];
        for (var k = 0; k < FrequencyBins; k++)
        {
            var fftFreq = (double)(SampleRate / 2) * k / (FrequencyBins - 1);
            for (var m = 0; m < MelBins; m++)
            {
                var down = -(filterFreqs[m] - fftFreq) / (filterFreqs[m + 1] - filterFreqs[m]);
                var up = (filterFreqs[m + 2] - fftFreq) / (filterFreqs[m + 2] - filterFreqs[m + 1]);
                filters[k * MelBins + m] = Math.Max(0.0, Math.Min(down, up));
            }
        }

        return filters;
    }

    /// <summary>In-place iterative radix-2 complex FFT (length must be a power of two).</summary>
    internal static class Fft
    {
        public static void Forward(double[] re, double[] im)
        {
            var n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (var len = 2; len <= n; len <<= 1)
            {
                var angle = -2 * Math.PI / len;
                var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (var i = 0; i < n; i += len)
                {
                    var w = Complex.One;
                    for (var j = 0; j < len / 2; j++)
                    {
                        var ur = re[i + j];
                        var ui = im[i + j];
                        var vr = re[i + j + len / 2] * w.Real - im[i + j + len / 2] * w.Imaginary;
                        var vi = re[i + j + len / 2] * w.Imaginary + im[i + j + len / 2] * w.Real;
                        re[i + j] = ur + vr;
                        im[i + j] = ui + vi;
                        re[i + j + len / 2] = ur - vr;
                        im[i + j + len / 2] = ui - vi;
                        w *= wlen;
                    }
                }
            }
        }
    }
}
