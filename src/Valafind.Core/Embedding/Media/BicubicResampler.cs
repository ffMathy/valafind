namespace Valafind.Core.Embedding.Media;

/// <summary>
/// Bicubic resize of 8-bit RGB images with antialiasing, a port of Pillow's <c>ImagingResample</c> (Resample.c):
/// separable convolution, horizontal pass first, 8-bit intermediate, 22-bit fixed-point weights. The model's reference
/// preprocessing (torchvision <c>resize(antialias=True, BICUBIC)</c> on uint8) implements the same algorithm, so the
/// pixels it sees match the reference within ±2 levels.
/// </summary>
internal static class BicubicResampler
{
    private const int PrecisionBits = 32 - 8 - 2;
    private const double Support = 2.0;

    /// <summary>Resizes <paramref name="rgb"/> (<paramref name="width"/> × <paramref name="height"/>) to
    /// <paramref name="newWidth"/> × <paramref name="newHeight"/>.</summary>
    public static byte[] Resize(byte[] rgb, int width, int height, int newWidth, int newHeight)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        ArgumentOutOfRangeException.ThrowIfLessThan(newWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(newHeight, 1);
        if (rgb.LongLength != (long)width * height * 3)
            throw new ArgumentException("Buffer size does not match the image size.", nameof(rgb));
        if (newWidth == width && newHeight == height)
            return (byte[])rgb.Clone();

        var needHorizontal = newWidth != width;
        var needVertical = newHeight != height;

        var (vBounds, vKernel, vSize) = Coefficients(height, newHeight);
        var (hBounds, hKernel, hSize) = Coefficients(width, newWidth);

        // Rows of the input the vertical pass reads.
        var firstRow = vBounds[0];
        var lastRow = vBounds[(newHeight - 1) * 2] + vBounds[(newHeight - 1) * 2 + 1];

        byte[] temp;
        int tempWidth, tempHeight, rowOffset;
        if (needHorizontal)
        {
            tempWidth = newWidth;
            tempHeight = needVertical ? lastRow - firstRow : height;
            rowOffset = needVertical ? firstRow : 0;
            temp = new byte[tempWidth * tempHeight * 3];
            Horizontal(rgb, width, temp, tempWidth, tempHeight, rowOffset, hBounds, hKernel, hSize);
            if (needVertical)
            {
                for (var i = 0; i < newHeight; i++)
                    vBounds[i * 2] -= firstRow;
            }
        }
        else
        {
            temp = rgb;
            tempWidth = width;
            tempHeight = height;
        }

        if (!needVertical)
            return temp;

        var output = new byte[tempWidth * newHeight * 3];
        Vertical(temp, tempWidth, output, newHeight, vBounds, vKernel, vSize);
        return output;
    }

    private static double Filter(double x)
    {
        const double a = -0.5;
        if (x < 0)
            x = -x;
        if (x < 1.0)
            return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0)
            return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    /// <summary>Pillow's precompute_coeffs + normalize_coeffs_8bpc: per output pixel the first input index and count
    /// (bounds) and fixed-point weights (kernel, <c>kernelSize</c> per output pixel).</summary>
    private static (int[] Bounds, int[] Kernel, int KernelSize) Coefficients(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = Support * filterScale;
        var kernelSize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var kernel = new int[outSize * kernelSize];
        var weights = new double[kernelSize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ss = 1.0 / filterScale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0)
                xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
                xmax = inSize;
            xmax -= xmin;

            var ww = 0.0;
            for (var x = 0; x < xmax; x++)
            {
                var w = Filter((x + xmin - center + 0.5) * ss);
                weights[x] = w;
                ww += w;
            }

            var row = xx * kernelSize;
            for (var x = 0; x < kernelSize; x++)
            {
                var k = x < xmax && ww != 0.0 ? weights[x] / ww : 0.0;
                kernel[row + x] = k < 0
                    ? (int)(-0.5 + k * (1 << PrecisionBits))
                    : (int)(0.5 + k * (1 << PrecisionBits));
            }

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        return (bounds, kernel, kernelSize);
    }

    private static byte Clip(int value)
    {
        if (value >= (1 << PrecisionBits) * 256)
            return 255;
        if (value <= 0)
            return 0;
        return (byte)(value >> PrecisionBits);
    }

    private static void Horizontal(byte[] input, int inWidth, byte[] output, int outWidth, int rows, int rowOffset,
        int[] bounds, int[] kernel, int kernelSize)
    {
        for (var y = 0; y < rows; y++)
        {
            var inRow = (y + rowOffset) * inWidth * 3;
            var outRow = y * outWidth * 3;
            for (var xx = 0; xx < outWidth; xx++)
            {
                var xmin = bounds[xx * 2];
                var xmax = bounds[xx * 2 + 1];
                var k = xx * kernelSize;
                int r = 1 << (PrecisionBits - 1), g = r, b = r;
                var p = inRow + xmin * 3;
                for (var x = 0; x < xmax; x++, p += 3)
                {
                    var w = kernel[k + x];
                    r += input[p] * w;
                    g += input[p + 1] * w;
                    b += input[p + 2] * w;
                }

                var o = outRow + xx * 3;
                output[o] = Clip(r);
                output[o + 1] = Clip(g);
                output[o + 2] = Clip(b);
            }
        }
    }

    private static void Vertical(byte[] input, int width, byte[] output, int outHeight, int[] bounds, int[] kernel, int kernelSize)
    {
        var stride = width * 3;
        for (var yy = 0; yy < outHeight; yy++)
        {
            var ymin = bounds[yy * 2];
            var ymax = bounds[yy * 2 + 1];
            var k = yy * kernelSize;
            var outRow = yy * stride;
            for (var x = 0; x < stride; x++)
            {
                var sum = 1 << (PrecisionBits - 1);
                var p = ymin * stride + x;
                for (var y = 0; y < ymax; y++, p += stride)
                    sum += input[p] * kernel[k + y];
                output[outRow + x] = Clip(sum);
            }
        }
    }
}
