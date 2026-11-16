namespace Valafind.Core.Embedding;

/// <summary>Matryoshka representation learning (MRL) helpers.</summary>
internal static class Matryoshka
{
    /// <summary>Keeps the first <paramref name="dimensions"/> components and L2-normalises them.
    /// A zero vector stays zero (never NaN).</summary>
    public static float[] TruncateAndNormalize(ReadOnlySpan<float> vector, int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        if (dimensions > vector.Length)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, $"Vector has only {vector.Length} components.");

        var result = vector[..dimensions].ToArray();
        double sum = 0;
        foreach (var x in result)
            sum += (double)x * x;
        if (sum > 0 && double.IsFinite(sum))
        {
            var inv = (float)(1.0 / Math.Sqrt(sum));
            for (var i = 0; i < result.Length; i++)
                result[i] *= inv;
        }
        return result;
    }
}
