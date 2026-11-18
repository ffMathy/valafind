using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Valafind.Core.Abstractions;
using Valafind.Core.Extraction;

namespace Valafind.App.Services;

/// <summary>What the details pane shows for a file: a picture, the start of its text, or nothing.</summary>
/// <param name="Image">The picture, scaled down to fit the pane, or null.</param>
/// <param name="Text">The first part of the file's text, or null.</param>
/// <param name="IsMonospace">True when <paramref name="Text"/> is code or data that reads best in a fixed-width font.</param>
public sealed record FilePreview(Bitmap? Image, string? Text, bool IsMonospace)
{
    /// <summary>Nothing to show.</summary>
    public static FilePreview None { get; } = new(null, null, false);
}

/// <summary>Loads row thumbnails and details-pane previews. Never throws for unreadable or unsupported files (returns
/// null / <see cref="FilePreview.None"/>); only <see cref="OperationCanceledException"/> for the caller's token propagates.</summary>
public interface IFilePreviewService
{
    /// <summary>True when <see cref="LoadThumbnailAsync"/> may return a picture for this file (cheap: by kind and
    /// extension only), so the launcher only queues work that can succeed.</summary>
    bool HasThumbnail(string path, FileKind kind);

    /// <summary>A small picture of the file for the result row, or null.</summary>
    Task<Bitmap?> LoadThumbnailAsync(string path, FileKind kind, CancellationToken cancellationToken);

    /// <summary>The preview for the details pane.</summary>
    Task<FilePreview> LoadPreviewAsync(string path, FileKind kind, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IFilePreviewService"/> on SkiaSharp (pictures, the library Avalonia already renders with) and Core's
/// <see cref="DocumentReader"/> (text of documents, PDFs, mail, code, …). Pictures are turned upright by their EXIF
/// orientation, keep their transparency and are subsampled while decoding, so a 50-megapixel photo costs a few MB.
/// Recent results are cached by path, size and modification time, so retyping a query does not decode again.
/// </summary>
public sealed class FilePreviewService : IFilePreviewService, IDisposable
{
    /// <summary>Shorter side of row thumbnails in pixels (rows draw them at up to 38 px, twice that on HiDPI).</summary>
    internal const int ThumbnailSide = 96;

    /// <summary>Longest side of the pane preview in pixels (the pane is ~400 px wide, twice that on HiDPI).</summary>
    internal const int PreviewSide = 1024;

    /// <summary>Characters of text shown in the pane.</summary>
    internal const int MaxPreviewChars = 4_000;

    /// <summary>Lines of text shown in the pane.</summary>
    internal const int MaxPreviewLines = 80;

    /// <summary>Pictures with more pixels than this are not decoded (a malicious header can claim anything).</summary>
    private const long MaxPixels = 200_000_000;

    /// <summary>Pictures larger than this are not opened for a preview.</summary>
    private const long MaxImageBytes = 256L * 1024 * 1024;

    private static readonly HashSet<string> DecodableImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".wbmp", ".heic", ".heif", ".avif",
    };

    private static readonly HashSet<string> MonospaceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".log", ".csv", ".tsv", ".ipynb", ".tex",
    };

    private readonly IDocumentReader _reader;
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private readonly LruCache<Bitmap> _thumbnails = new(96);
    private readonly LruCache<FilePreview> _previews = new(8);

    /// <summary>Creates the service with the built-in extractors and a short per-file budget (a preview that takes
    /// longer than a few seconds is not worth waiting for).</summary>
    public FilePreviewService()
        : this(new DocumentReader(DocumentReader.CreateDefaultExtractors(), perFileTimeout: TimeSpan.FromSeconds(5)))
    {
    }

    internal FilePreviewService(IDocumentReader reader) => _reader = reader;

    /// <inheritdoc />
    public bool HasThumbnail(string path, FileKind kind) =>
        kind == FileKind.Image && DecodableImages.Contains(Path.GetExtension(path));

    /// <inheritdoc />
    public async Task<Bitmap?> LoadThumbnailAsync(string path, FileKind kind, CancellationToken cancellationToken)
    {
        if (!HasThumbnail(path, kind))
            return null;
        var key = CacheKey(path);
        if (key is null)
            return null;
        if (_thumbnails.TryGet(key, out var cached))
            return cached;
        var bitmap = await DecodeAsync(path, ThumbnailSide, fill: true, cancellationToken).ConfigureAwait(false);
        if (bitmap is not null)
            _thumbnails.Add(key, bitmap);
        return bitmap;
    }

    /// <inheritdoc />
    public async Task<FilePreview> LoadPreviewAsync(string path, FileKind kind, CancellationToken cancellationToken)
    {
        if (kind is FileKind.Audio or FileKind.Video or FileKind.Archive)
            return FilePreview.None;
        var key = CacheKey(path);
        if (key is null)
            return FilePreview.None;
        if (_previews.TryGet(key, out var cached))
            return cached;

        FilePreview preview;
        if (kind == FileKind.Image)
        {
            var image = HasThumbnail(path, kind)
                ? await DecodeAsync(path, PreviewSide, fill: false, cancellationToken).ConfigureAwait(false)
                : null;
            preview = image is null ? FilePreview.None : new FilePreview(image, null, false);
        }
        else
        {
            var extracted = await Task.Run(() => _reader.ReadAsync(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            var text = extracted is null ? string.Empty : Excerpt(extracted.Text);
            preview = text.Length == 0 ? FilePreview.None : new FilePreview(null, text, IsMonospace(path, kind));
        }

        _previews.Add(key, preview);
        return preview;
    }

    /// <summary>True when the text of this file reads best in a fixed-width font.</summary>
    internal static bool IsMonospace(string path, FileKind kind) =>
        kind is FileKind.Code or FileKind.Notebook || MonospaceExtensions.Contains(Path.GetExtension(path));

    /// <summary>The start of <paramref name="text"/> for the pane: line endings unified, trailing spaces and runs of blank
    /// lines dropped, cut at <see cref="MaxPreviewLines"/> lines or <see cref="MaxPreviewChars"/> characters (at a word
    /// boundary where possible) with "…" where it was cut. Indentation is kept (code).</summary>
    internal static string Excerpt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, MaxPreviewChars + 1));
        var lines = 0;
        var blank = 0;
        var cut = false;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.TrimEnd();
            if (line.IsWhiteSpace())
            {
                blank++;
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append('\n', blank > 0 ? 2 : 1);
                lines += blank > 0 ? 2 : 1;
            }
            blank = 0;
            if (lines >= MaxPreviewLines || builder.Length + line.Length > MaxPreviewChars)
            {
                var room = MaxPreviewChars - builder.Length;
                if (lines < MaxPreviewLines && room > 0)
                {
                    var part = line[..Math.Min(room, line.Length)];
                    var space = part.LastIndexOf(' ');
                    builder.Append(space > room / 2 ? part[..space] : part);
                }
                cut = true;
                break;
            }
            builder.Append(line);
        }
        var result = builder.ToString().TrimEnd();
        return cut ? result + " …" : result;
    }

    private static string? CacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{path}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<Bitmap?> DecodeAsync(string path, int side, bool fill, CancellationToken cancellationToken)
    {
        await _decodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Decode(path, side, fill, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Corrupt, locked or vanished files (and absurd sizes) just keep their icon.
            return null;
        }
        finally
        {
            _decodeSlots.Release();
        }
    }

    /// <summary>Decodes a picture upright. <paramref name="fill"/>: the shorter side becomes <paramref name="side"/>
    /// (the row crops it to a square); otherwise the longer side does. Pictures are never scaled up.</summary>
    internal static Bitmap? Decode(string path, int side, bool fill, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > MaxImageBytes)
            return null;
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (uprightWidth, uprightHeight) = swap ? (info.Height, info.Width) : (info.Width, info.Height);
        var reference = fill ? Math.Min(uprightWidth, uprightHeight) : Math.Max(uprightWidth, uprightHeight);
        var scale = Math.Min(1.0, (double)side / reference);
        var outWidth = Math.Max(1, (int)Math.Round(uprightWidth * scale));
        var outHeight = Math.Max(1, (int)Math.Round(uprightHeight * scale));

        // Subsample while decoding (JPEG: 1/2, 1/4, 1/8) as long as the result is still at least the output size.
        var (needWidth, needHeight) = swap ? (outHeight, outWidth) : (outWidth, outHeight);
        var size = new SKSizeI(info.Width, info.Height);
        for (var factor = 8; factor >= 2; factor /= 2)
        {
            var sampled = codec.GetScaledDimensions(1f / factor);
            if (sampled.Width >= needWidth && sampled.Height >= needHeight && sampled.Width < info.Width)
            {
                size = sampled;
                break;
            }
        }

        var decodeInfo = new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.Transparent);
            var upright = Orientation(origin, size.Width, size.Height);
            var fit = SKMatrix.CreateScale((float)outWidth / (swap ? size.Height : size.Width),
                (float)outHeight / (swap ? size.Width : size.Height));
            canvas.SetMatrix(SKMatrix.Concat(fit, upright));
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, output.GetPixels(), new PixelSize(outWidth, outHeight),
            new Vector(96, 96), output.RowBytes);
    }

    /// <summary>Maps stored pixels (<paramref name="width"/> × <paramref name="height"/>) to the upright picture for an
    /// EXIF orientation.</summary>
    internal static SKMatrix Orientation(SKEncodedOrigin origin, float width, float height) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <inheritdoc />
    public void Dispose() => _decodeSlots.Dispose();

    /// <summary>A small thread-safe least-recently-used cache.</summary>
    private sealed class LruCache<T>(int capacity)
    {
        private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, T Value)> _order = new();
        private readonly Lock _gate = new();

        public bool TryGet(string key, out T value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = default!;
            return false;
        }

        public void Add(string key, T value)
        {
            lock (_gate)
            {
                if (_map.Remove(key, out var existing))
                    _order.Remove(existing);
                _map[key] = _order.AddFirst((key, value));
                while (_map.Count > capacity && _order.Last is { } last)
                {
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                }
            }
        }
    }
}
