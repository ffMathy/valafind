using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Valafind.App.Services;
using Valafind.App.Tests.Headless;
using Valafind.Core.Abstractions;
using Xunit;

namespace Valafind.App.Tests.Services;

/// <summary>Excerpts of file text for the details pane (no UI needed).</summary>
public sealed class FilePreviewExcerptTests
{
    [Fact]
    public void ExcerptKeepsIndentationAndCollapsesBlankRuns()
    {
        var text = "class A\r\n{\r\n    int x;   \r\n\r\n\r\n\r\n    int y;\r\n}\r\n\r\n";
        Assert.Equal("class A\n{\n    int x;\n\n    int y;\n}", FilePreviewService.Excerpt(text));
    }

    [Fact]
    public void ExcerptIsEmptyForBlankText() => Assert.Equal(string.Empty, FilePreviewService.Excerpt(" \n\t\n "));

    [Fact]
    public void ExcerptCutsLongTextAtAWordAndMarksTheCut()
    {
        var text = string.Join(' ', Enumerable.Repeat("lease", 2_000));
        var excerpt = FilePreviewService.Excerpt(text);
        Assert.True(excerpt.Length <= FilePreviewService.MaxPreviewChars + 2, $"length {excerpt.Length}");
        Assert.EndsWith("lease …", excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcerptStopsAfterTheLineLimit()
    {
        var text = string.Join('\n', Enumerable.Range(1, 500).Select(i => "line " + i));
        var excerpt = FilePreviewService.Excerpt(text);
        Assert.Equal(FilePreviewService.MaxPreviewLines, excerpt.Split('\n').Length);
        Assert.StartsWith("line 1\nline 2\n", excerpt, StringComparison.Ordinal);
        Assert.EndsWith(" …", excerpt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/a/Program.cs", FileKind.Code, true)]
    [InlineData("/a/config.json", FileKind.Text, true)]
    [InlineData("/a/notes.md", FileKind.Text, false)]
    [InlineData("/a/lease.pdf", FileKind.Pdf, false)]
    public void CodeAndDataUseAFixedWidthFont(string path, FileKind kind, bool expected) =>
        Assert.Equal(expected, FilePreviewService.IsMonospace(path, kind));

    [Theory]
    [InlineData("/p/IMG_0042.JPG", FileKind.Image, true)]
    [InlineData("/p/scan.webp", FileKind.Image, true)]
    [InlineData("/p/drawing.svg", FileKind.Image, false)]
    [InlineData("/p/lease.pdf", FileKind.Pdf, false)]
    public void OnlyDecodablePicturesGetThumbnails(string path, FileKind kind, bool expected)
    {
        using var service = new FilePreviewService();
        Assert.Equal(expected, service.HasThumbnail(path, kind));
    }

    [Fact]
    public async Task TextPreviewComesFromTheDocumentReader()
    {
        var path = Path.Combine(Path.GetTempPath(), "valafind-preview-" + Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(path, "namespace Demo;\n\npublic sealed class Lease\n{\n    public decimal Deposit { get; init; }\n}\n",
            TestContext.Current.CancellationToken);
        try
        {
            using var service = new FilePreviewService();
            var preview = await service.LoadPreviewAsync(path, FileKind.Code, TestContext.Current.CancellationToken);
            Assert.Null(preview.Image);
            Assert.True(preview.IsMonospace);
            Assert.StartsWith("namespace Demo;\n\npublic sealed class Lease", preview.Text, StringComparison.Ordinal);
            Assert.Contains("    public decimal Deposit", preview.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CorruptPicturesHaveNoThumbnail()
    {
        var path = Path.Combine(Path.GetTempPath(), "valafind-preview-" + Guid.NewGuid().ToString("N") + ".jpg");
        await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            using var service = new FilePreviewService();
            Assert.Null(await service.LoadThumbnailAsync(path, FileKind.Image, TestContext.Current.CancellationToken));
            Assert.Same(FilePreview.None, await service.LoadPreviewAsync(path, FileKind.Image, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingFilesHaveNoPreview()
    {
        using var service = new FilePreviewService();
        var preview = await service.LoadPreviewAsync("/nonexistent/valafind/lease.pdf", FileKind.Pdf, TestContext.Current.CancellationToken);
        Assert.Same(FilePreview.None, preview);
        Assert.Null(await service.LoadThumbnailAsync("/nonexistent/valafind/a.png", FileKind.Image, TestContext.Current.CancellationToken));
    }
}

/// <summary>Picture decoding (Avalonia bitmaps need the headless platform).</summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class FilePreviewDecodeTests(HeadlessFixture fixture)
{
    private static string Rotated => Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotated.jpg");

    /// <summary>Writes a <paramref name="width"/> × <paramref name="height"/> PNG: transparent left half, opaque red right half.</summary>
    internal static string WritePng(int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), "valafind-preview-" + Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }
        return (buffer[0], buffer[1], buffer[2], buffer[3]);
    }

    [Fact]
    public Task ThumbnailsFillTheirShorterSideAndKeepTransparency() =>
        fixture.Session.Dispatch(() =>
        {
            var path = WritePng(800, 400);
            try
            {
                var thumbnail = FilePreviewService.Decode(path, FilePreviewService.ThumbnailSide, fill: true, CancellationToken.None)!;
                Assert.Equal(new PixelSize(192, 96), thumbnail.PixelSize);
                Assert.Equal(0, Pixel(thumbnail, 10, 48).A);
                var right = Pixel(thumbnail, 180, 48);
                Assert.True(right is { A: 255, R: > 240, G: < 10, B: < 10 }, $"right {right}");
            }
            finally
            {
                File.Delete(path);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task PreviewsFitTheirLongerSideAndAreNeverScaledUp() =>
        fixture.Session.Dispatch(() =>
        {
            var large = WritePng(3000, 1500);
            var small = WritePng(120, 80);
            try
            {
                Assert.Equal(new PixelSize(1024, 512), FilePreviewService.Decode(large, FilePreviewService.PreviewSide, fill: false, CancellationToken.None)!.PixelSize);
                Assert.Equal(new PixelSize(120, 80), FilePreviewService.Decode(small, FilePreviewService.PreviewSide, fill: false, CancellationToken.None)!.PixelSize);
            }
            finally
            {
                File.Delete(large);
                File.Delete(small);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task PicturesAreTurnedUprightByTheirExifOrientation() =>
        fixture.Session.Dispatch(() =>
        {
            // Stored 300×200 with orientation 6: upright it is 200×300 and the blue bar stored on the left is at the top.
            var image = FilePreviewService.Decode(Rotated, FilePreviewService.PreviewSide, fill: false, CancellationToken.None)!;
            Assert.Equal(new PixelSize(200, 300), image.PixelSize);
            var top = Pixel(image, 100, 60);
            var bottom = Pixel(image, 100, 260);
            Assert.True(top.B > 150 && top.R < 80, $"top {top}");
            Assert.True(bottom is { R: > 200, G: > 200, B: > 200 }, $"bottom {bottom}");
        }, TestContext.Current.CancellationToken);
}
