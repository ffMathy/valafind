using Valafind.Core.Abstractions;

namespace Valafind.Core.Embedding.Media;

/// <summary>Patches of one picture or frame for the vision encoder.</summary>
/// <param name="PixelValues">[patches × 768]: each 16×16 patch row by row, each pixel R, G, B in [0, 1].</param>
/// <param name="PositionIds">[patches × 2]: (column, row) of each patch.</param>
/// <param name="Patches">Number of patches (a multiple of 9).</param>
internal sealed record VisionPatches(float[] PixelValues, long[] PositionIds, int Patches);

/// <summary>
/// Port of the EmbeddingGemma 2 (Gemma 4) image and video processors: aspect-ratio preserving resize so the picture
/// fits <c>max_soft_tokens × 3²</c> patches of 16×16 pixels with both sides a multiple of 48 (it may scale up), bicubic
/// antialiased resampling, rescale to [0, 1] (no mean/std normalisation), patchify, and (x, y) patch positions.
/// Padding to the full patch budget is skipped: the encoder returns the same soft tokens without it.
/// </summary>
internal static class VisionPreprocessor
{
    public const int PatchSize = 16;
    public const int PoolingKernelSize = 3;
    public const int PatchPixels = PatchSize * PatchSize * 3;

    /// <summary>Soft-token budgets the vision encoder was trained with.</summary>
    public static readonly int[] SupportedSoftTokens = [70, 140, 280, 560, 1120];

    public static VisionPatches Preprocess(DecodedImage image, int maxSoftTokens)
    {
        ArgumentNullException.ThrowIfNull(image);
        image.Validate();
        var (targetHeight, targetWidth) = TargetSize(image.Height, image.Width, maxSoftTokens * PoolingKernelSize * PoolingKernelSize);
        var pixels = BicubicResampler.Resize(image.Rgb, image.Width, image.Height, targetWidth, targetHeight);
        return Patchify(pixels, targetWidth, targetHeight);
    }

    /// <summary>
    /// Python <c>get_aspect_ratio_preserving_size</c>: the largest size, sides multiples of 48, whose patch count fits
    /// <paramref name="maxPatches"/>. Very thin pictures get one 48-pixel side and the other side from the aspect ratio
    /// (capped).
    /// </summary>
    public static (int Height, int Width) TargetSize(int height, int width, int maxPatches)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        var totalPx = (double)height * width;
        var targetPx = (double)maxPatches * PatchSize * PatchSize;
        var factor = Math.Sqrt(targetPx / totalPx);
        var idealHeight = factor * height;
        var idealWidth = factor * width;
        const int sideMult = PoolingKernelSize * PatchSize;

        var targetHeight = (int)Math.Floor(idealHeight / sideMult) * sideMult;
        var targetWidth = (int)Math.Floor(idealWidth / sideMult) * sideMult;
        if (targetHeight == 0 && targetWidth == 0)
            throw new ArgumentException($"Cannot resize a {width}x{height} picture to the patch grid.");

        var maxSideLength = maxPatches / (PoolingKernelSize * PoolingKernelSize) * sideMult;
        if (targetHeight == 0)
        {
            targetHeight = sideMult;
            targetWidth = (int)Math.Min((long)Math.Floor((double)width / height) * sideMult, maxSideLength);
        }
        else if (targetWidth == 0)
        {
            targetWidth = sideMult;
            targetHeight = (int)Math.Min((long)Math.Floor((double)height / width) * sideMult, maxSideLength);
        }

        return (targetHeight, targetWidth);
    }

    private static VisionPatches Patchify(byte[] rgb, int width, int height)
    {
        var rows = height / PatchSize;
        var columns = width / PatchSize;
        var patches = rows * columns;
        var values = new float[patches * PatchPixels];
        var positions = new long[patches * 2];
        const float scale = 1f / 255f;
        var stride = width * 3;
        var i = 0;
        for (var pr = 0; pr < rows; pr++)
        {
            for (var pc = 0; pc < columns; pc++)
            {
                var patch = pr * columns + pc;
                positions[patch * 2] = pc;
                positions[patch * 2 + 1] = pr;
                for (var y = 0; y < PatchSize; y++)
                {
                    var p = (pr * PatchSize + y) * stride + pc * PatchSize * 3;
                    for (var x = 0; x < PatchSize * 3; x++)
                        values[i++] = rgb[p + x] * scale;
                }
            }
        }

        return new VisionPatches(values, positions, patches);
    }
}
