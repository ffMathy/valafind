using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Valafind.App.Controls;
using Valafind.App.Services;
using Valafind.App.Tests.Services;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>Row thumbnails for pictures and the details-pane preview, from the view model to the rendered launcher.</summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class FilePreviewLauncherTests(HeadlessFixture fixture)
{
    /// <summary>Hands out a fixed picture for pictures and fixed text for everything else, and records the requests.</summary>
    private sealed class FakePreviews(Bitmap picture) : IFilePreviewService
    {
        public ConcurrentQueue<string> Thumbnails { get; } = new();
        public ConcurrentQueue<string> Previews { get; } = new();

        public bool HasThumbnail(string path, FileKind kind) => kind == FileKind.Image;

        public Task<Bitmap?> LoadThumbnailAsync(string path, FileKind kind, CancellationToken cancellationToken)
        {
            Thumbnails.Enqueue(path);
            return Task.FromResult<Bitmap?>(picture);
        }

        public Task<FilePreview> LoadPreviewAsync(string path, FileKind kind, CancellationToken cancellationToken)
        {
            Previews.Enqueue(path);
            return Task.FromResult(kind == FileKind.Image
                ? new FilePreview(picture, null, false)
                : new FilePreview(null, "Lejekontrakt\n\n§ 1 Parterne\nUdlejer: Bolig ApS\nLejer: Anna Jensen", false));
        }
    }

    private static SearchResult Hit(string name, FileKind kind, float score) =>
        new("/home/u/" + name, name, "/home/u", kind, DateTimeOffset.UtcNow.AddDays(-3), 1234, score,
            kind == FileKind.Image ? string.Empty : "the deposit is three months of rent", []);

    /// <summary>A 640×480 "photo": sky, sun and hills, decoded the same way real pictures are.</summary>
    private static Bitmap SamplePicture()
    {
        var path = Path.Combine(Path.GetTempPath(), "valafind-preview-" + Guid.NewGuid().ToString("N") + ".png");
        using (var bitmap = new SkiaSharp.SKBitmap(640, 480))
        {
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                using var sky = new SkiaSharp.SKPaint
                {
                    Shader = SkiaSharp.SKShader.CreateLinearGradient(new(0, 0), new(0, 480),
                        [new SkiaSharp.SKColor(0x4A, 0x90, 0xD9), new SkiaSharp.SKColor(0xF6, 0xC1, 0x8A)], SkiaSharp.SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(0, 0, 640, 480, sky);
                using var sun = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFF, 0xE0, 0x82), IsAntialias = true };
                canvas.DrawCircle(470, 170, 60, sun);
                using var hill = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x2F, 0x6B, 0x3A), IsAntialias = true };
                canvas.DrawOval(180, 520, 360, 200, hill);
                using var hill2 = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x3F, 0x8A, 0x4B), IsAntialias = true };
                canvas.DrawOval(560, 540, 300, 180, hill2);
            }
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        try
        {
            return FilePreviewService.Decode(path, FilePreviewService.PreviewSide, fill: false, CancellationToken.None)!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public Task PicturesGetThumbnailsAndTheSelectedRowItsPreview() =>
        fixture.Session.Dispatch(async () =>
        {
            var previews = new FakePreviews(SamplePicture());
            using var vm = new LauncherViewModel(UiFlavor.Linux, new ValafindSettings(), TimeProvider.System, static a => a(), "/home/u")
            {
                Previews = previews,
            };
            vm.ApplyResults("lease", [Hit("lease.pdf", FileKind.Pdf, 0.8f), Hit("keys.jpg", FileKind.Image, 0.7f), Hit("plan.png", FileKind.Image, 0.6f)],
                TimeSpan.FromMilliseconds(5));

            await WaitUntil(() => vm.Results[1].HasThumbnail && vm.Results[2].HasThumbnail && vm.Results[0].HasPreviewText);
            Assert.False(vm.Results[0].HasThumbnail);
            Assert.Equal(["/home/u/keys.jpg", "/home/u/plan.png"], previews.Thumbnails.Order());
            Assert.Equal(["/home/u/lease.pdf"], previews.Previews);
            Assert.StartsWith("Lejekontrakt", vm.Results[0].PreviewText, StringComparison.Ordinal);

            vm.MoveDownCommand.Execute(null);
            await WaitUntil(() => vm.Results[1].HasPreviewImage);
            Assert.Null(vm.Results[1].PreviewText);

            // Each preview loads once; reselecting a row reuses it.
            vm.MoveUpCommand.Execute(null);
            vm.MoveDownCommand.Execute(null);
            Assert.Equal(2, previews.Previews.Count);
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task NoPreviewIsLoadedWhileThePaneIsHidden() =>
        fixture.Session.Dispatch(async () =>
        {
            var previews = new FakePreviews(SamplePicture());
            using var vm = new LauncherViewModel(UiFlavor.Windows, new ValafindSettings(), TimeProvider.System, static a => a(), "/home/u")
            {
                Previews = previews,
                IsPreviewVisible = false,
            };
            vm.ApplyResults("lease", [Hit("lease.pdf", FileKind.Pdf, 0.8f)], TimeSpan.FromMilliseconds(5));
            Assert.Empty(previews.Previews);

            vm.TogglePreviewCommand.Execute(null);
            await WaitUntil(() => vm.Results[0].HasPreviewText);
            Assert.Single(previews.Previews);
        }, TestContext.Current.CancellationToken);

    public static TheoryData<UiFlavor, string> Cases() => new()
    {
        { UiFlavor.Windows, "light" },
        { UiFlavor.Windows, "dark" },
        { UiFlavor.Mac, "light" },
        { UiFlavor.Mac, "dark" },
        { UiFlavor.Linux, "light" },
        { UiFlavor.Linux, "dark" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public Task RendersPictureThumbnailsAndPreviews(UiFlavor flavor, string theme) =>
        fixture.Session.Dispatch(() =>
        {
            var picture = SamplePicture();
            var vm = DesignData.Create(flavor);
            var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

            // Text preview of the top match.
            var top = vm.Results[0];
            top.ApplyPreview(new FilePreview(null,
                "LEJEKONTRAKT\n\n§ 1. Parterne og det lejede\nUdlejer: Bolig ApS, Nørrebrogade 12, 2200 København N\n" +
                "Lejer: Anna Jensen\n\n§ 2. Lejens størrelse\nDen årlige leje udgør 132.000 kr.\n\n§ 3. Depositum\n" +
                "Lejer betaler et depositum svarende til tre måneders leje, i alt 33.000 kr., der tilbagebetales ved fraflytning.",
                false));
            foreach (var item in vm.Results.Where(r => r.Kind == FileKind.Image))
                item.Thumbnail = picture;
            var frame = LauncherScreenshotTests.Render(flavor, vm, variant, out var view);
            Assert.Contains(view.GetVisualDescendants().OfType<FileKindIcon>(), i => i.Thumbnail is not null);
            LauncherScreenshotTests.Save(frame, $"{flavor.ToString().ToLowerInvariant()}-preview-text-{theme}.png");

            // Picture preview of a selected photo.
            var photo = vm.Results.First(r => r.Kind == FileKind.Image);
            vm.Select(photo);
            photo.ApplyPreview(new FilePreview(picture, null, false));
            frame = LauncherScreenshotTests.Render(flavor, vm, variant, out view);
            Assert.Contains(view.GetVisualDescendants().OfType<Image>(), i => i.IsEffectivelyVisible && ReferenceEquals(i.Source, picture));
            LauncherScreenshotTests.Save(frame, $"{flavor.ToString().ToLowerInvariant()}-preview-picture-{theme}.png");
        }, TestContext.Current.CancellationToken);
}
