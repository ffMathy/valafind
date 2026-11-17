using SkiaSharp;
using Valafind.Core.Abstractions;
using Valafind.Core.Embedding.Media;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Decodes PNG, JPEG, GIF (first frame), BMP, WebP and ICO with SkiaSharp (the library Avalonia already ships, so it
/// adds nothing to the app). The result is RGB with alpha dropped (like Pillow's <c>convert("RGB")</c>), turned upright
/// by its EXIF orientation (like <c>ImageOps.exif_transpose</c> in the reference loader), and scaled down to fit
/// <c>maxSide</c>. Large JPEGs are subsampled while decoding so a 50-megapixel photo never needs 150 MB.
/// </summary>
internal static class SkiaImageDecoder
{
    /// <summary>Pictures with more pixels than this are not decoded (a malicious header can claim anything).</summary>
    internal const long MaxPixels = 200_000_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
    };

    public static bool CanDecode(string path) => Extensions.Contains(Path.GetExtension(path));

    public static DecodedImage? Decode(string path, int maxSide, CancellationToken cancellationToken)
    {
        using var stream = ExtractionIO.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        // Subsample while decoding (JPEG supports 1/2, 1/4, 1/8) as long as the result still exceeds maxSide.
        var size = new SKSizeI(info.Width, info.Height);
        var longest = Math.Max(info.Width, info.Height);
        for (var factor = 8; factor >= 2; factor /= 2)
        {
            if (longest / factor < maxSide)
                continue;
            var sampled = codec.GetScaledDimensions(1f / factor);
            if (Math.Max(sampled.Width, sampled.Height) >= maxSide)
            {
                size = sampled;
                break;
            }
        }

        // No colour space: pixels are taken as stored, without ICC conversion, like Pillow (the reference loader).
        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        var rgba = bitmap.GetPixelSpan();
        var rgb = new byte[size.Width * size.Height * 3];
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }

        var image = Orientation.Apply(new DecodedImage(size.Width, size.Height, rgb), codec.EncodedOrigin);
        return Fit(image, maxSide);
    }

    /// <summary>Scales <paramref name="image"/> down (bicubic) so neither side exceeds <paramref name="maxSide"/>.</summary>
    internal static DecodedImage Fit(DecodedImage image, int maxSide)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide)
            return image;
        var scale = (double)maxSide / longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        return new DecodedImage(width, height, BicubicResampler.Resize(image.Rgb, image.Width, image.Height, width, height));
    }

    /// <summary>Applies an EXIF orientation to RGB pixels.</summary>
    internal static class Orientation
    {
        public static DecodedImage Apply(DecodedImage image, SKEncodedOrigin origin) => origin switch
        {
            SKEncodedOrigin.TopRight => Transform(image, false, (x, y, w, h) => (w - 1 - x, y)),
            SKEncodedOrigin.BottomRight => Transform(image, false, (x, y, w, h) => (w - 1 - x, h - 1 - y)),
            SKEncodedOrigin.BottomLeft => Transform(image, false, (x, y, w, h) => (x, h - 1 - y)),
            // Rotated cases: the output is h × w; map output (x, y) to the source pixel.
            SKEncodedOrigin.LeftTop => Transform(image, true, (x, y, w, h) => (y, x)),
            SKEncodedOrigin.RightTop => Transform(image, true, (x, y, w, h) => (y, h - 1 - x)),
            SKEncodedOrigin.RightBottom => Transform(image, true, (x, y, w, h) => (w - 1 - y, h - 1 - x)),
            SKEncodedOrigin.LeftBottom => Transform(image, true, (x, y, w, h) => (w - 1 - y, x)),
            _ => image,
        };

        private static DecodedImage Transform(DecodedImage image, bool swap, Func<int, int, int, int, (int X, int Y)> source)
        {
            int w = image.Width, h = image.Height;
            var outWidth = swap ? h : w;
            var outHeight = swap ? w : h;
            var output = new byte[image.Rgb.Length];
            for (var y = 0; y < outHeight; y++)
            {
                for (var x = 0; x < outWidth; x++)
                {
                    var (sx, sy) = source(x, y, w, h);
                    var s = (sy * w + sx) * 3;
                    var o = (y * outWidth + x) * 3;
                    output[o] = image.Rgb[s];
                    output[o + 1] = image.Rgb[s + 1];
                    output[o + 2] = image.Rgb[s + 2];
                }
            }

            return new DecodedImage(outWidth, outHeight, output);
        }
    }
}
