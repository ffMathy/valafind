using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Valafind.App.Services;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Xunit;

namespace Valafind.App.Tests.Headless;

/// <summary>
/// Renders the README and site screenshots (<c>docs/screenshots</c>): each launcher on a soft desktop backdrop, the
/// first-run screen of a build without the model, and the cover. They always render (so a broken layout fails here);
/// set <c>VALAFIND_DOCS_SCREENSHOT_DIR</c> to <c>docs/screenshots</c> to write the PNG files.
/// </summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class DocsScreenshotTests(HeadlessFixture fixture)
{
    private const string DirVariable = "VALAFIND_DOCS_SCREENSHOT_DIR";
    private const string Query = "where did we negotiate the deposit?";
    private const int ShotWidth = 1400;

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2026, 11, 18, 2, 3, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData(UiFlavor.Windows, "windows-launcher.png")]
    [InlineData(UiFlavor.Mac, "macos-launcher.png")]
    [InlineData(UiFlavor.Linux, "linux-launcher.png")]
    public Task RendersLauncherScreenshot(UiFlavor flavor, string file) =>
        fixture.Session.Dispatch(() =>
        {
            var vm = Launcher(flavor);
            var (scale, top, bottom) = flavor switch
            {
                UiFlavor.Windows => (1.05, 70.0, 26.0),
                UiFlavor.Mac => (1.35, 60.0, 0.0),
                _ => (1.0, 64.0, 24.0),
            };
            var frame = Capture(Shot(LauncherView(flavor, vm, scale, top, bottom), Backdrop(flavor)));
            Assert.Equal(ShotWidth, frame.PixelSize.Width);
            Save(frame, file);
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RendersFirstRunScreenshot() =>
        fixture.Session.Dispatch(() =>
        {
            var vm = new LauncherViewModel(UiFlavor.Linux, new ValafindSettings(), new FixedTime(), static a => a(), "/home/alex",
                "/opt/valafind");
            vm.SetModelMissing("/home/alex/.local/share/valafind/models/embeddinggemma-2");
            vm.UpdateStatus(IndexStatus.Initial with { State = IndexingState.ModelUnavailable });
            var frame = Capture(Shot(LauncherView(UiFlavor.Linux, vm, 1.0, 64, 24), Backdrop(UiFlavor.Linux)));
            Assert.Equal(ShotWidth, frame.PixelSize.Width);
            Save(frame, "first-run.png");
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RendersCover() =>
        fixture.Session.Dispatch(() =>
        {
            var small = Capture(Cover(Launcher(UiFlavor.Mac)));
            var large = Capture(Cover(Launcher(UiFlavor.Mac)), 2);
            Assert.Equal(new PixelSize(1280, 640), small.PixelSize);
            Assert.Equal(new PixelSize(2560, 1280), large.PixelSize);
            Save(small, "cover.png");
            Save(large, "cover@2x.png");
        }, TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ data

    /// <summary>The README story: an English question finds the landlord's e-mail and a Danish lease. The e-mail (top
    /// match) is selected, with the start of the mail in the preview; a photo of the deposit receipt shows its thumbnail.</summary>
    private static LauncherViewModel Launcher(UiFlavor flavor)
    {
        var home = flavor switch
        {
            UiFlavor.Windows => @"C:\Users\alex",
            UiFlavor.Mac => "/Users/alex",
            _ => "/home/alex",
        };
        var sep = flavor == UiFlavor.Windows ? "\\" : "/";
        string Dir(params string[] parts) => home + sep + string.Join(sep, parts);

        var vm = new LauncherViewModel(flavor, new ValafindSettings(), new FixedTime(), static a => a(), home,
            flavor == UiFlavor.Windows ? @"C:\Users\alex\AppData\Local\Programs\Valafind" : "/opt/valafind");
        SearchResult[] results =
        [
            Hit(Dir("Mail"), "Re- Depositum.eml", FileKind.Mail, 319, 0.59f,
                "…Hi Alex, We can accept two months of deposit instead of three if you sign the 24-month term. The rest of the lease stays as in the draft…",
                "We can accept two months of deposit instead of three if you sign the 24-month term."),
            Hit(Dir("Documents", "Notes"), "2025-09-12 landlord call.md", FileKind.Text, 1_204, 0.50f,
                "# Call with the landlord, 12 Sep 2025 He confirmed the deposit comes back in full after the move-out inspection, per clause 7 of the lease…",
                "He confirmed the deposit comes back in full after the move-out inspection"),
            Hit(Dir("Pictures", "Lejlighed"), "Depositum_kvittering.jpg", FileKind.Image, 2_811_004, 0.45f, string.Empty),
            Hit(Dir("Documents", "Bolig"), "Lejekontrakt_Nørrebrogade_2024.txt", FileKind.Text, 8_930, 0.43f,
                "…Lejen udgør 13.000 kr. pr. måned og betales forud den 1. i hver måned. § 7. Depositum Lejer betaler et depositum svarende til tre måneders leje…",
                "Depositum"),
            Hit(Dir("Documents", "Finance"), "budget_2025.csv", FileKind.Spreadsheet, 2_417, 0.36f,
                "Category,Monthly,Notes Rent (Nørrebrogade),13000,Deposit paid separately (39000) Utilities,1150,Electricity and water…",
                "Deposit paid separately"),
            Hit(Dir("Work", "Board"), "q3-retention-notes.md", FileKind.Text, 3_022, 0.27f,
                "# Q3 board update — retention Customer retention fell in Q3. Monthly churn rose from 2.1 % to 2.8 %, mostly annual plans…"),
            Hit(Dir("Documents"), "travel-lisbon-2025.md", FileKind.Text, 1_877, 0.22f,
                "# Lisbon, May 2025 Flights booked for 14–19 May. Hotel in Alfama, near the Miradouro de Santa Luzia…"),
        ];
        vm.LoadSnapshot(Query, results, TimeSpan.FromMilliseconds(flavor == UiFlavor.Linux ? 334 : 321));
        vm.UpdateStatus(DesignData.IdleStatus with { State = IndexingState.UpToDate, AllDone = 189_144, AllTotal = 189_144 });

        var photo = Photo();
        foreach (var item in vm.Results.Where(r => r.Kind == FileKind.Image))
            item.Thumbnail = photo;
        vm.Results.First(r => r.Kind == FileKind.Mail).ApplyPreview(new FilePreview(null,
            "Subject: Re: Depositum\nFrom: Henrik Larsen <henrik@example.com>\nTo: Alex <alex@example.com>\nDate: 2024-02-21 09:14 +01:00\n\n" +
            "Hi Alex,\n\nWe can accept two months of deposit instead of three if you sign the 24-month term. The rest of the lease " +
            "stays as in the draft we sent on 14 February.\n\nI will send the updated contract for signing this week, and the " +
            "keys can be handed over on 1 March.\n\nBest regards,\nHenrik Larsen\nBolig ApS",
            false));
        return vm;
    }

    private static SearchResult Hit(string dir, string name, FileKind kind, long size, float score, string snippet,
        string? highlight = null)
    {
        var sep = dir.Contains('\\') ? "\\" : "/";
        var spans = new List<TextSpan>();
        if (highlight is not null && snippet.IndexOf(highlight, StringComparison.Ordinal) is var i and >= 0)
            spans.Add(new TextSpan(i, highlight.Length));
        return new SearchResult(dir + sep + name, name, dir, kind, Modified, size, score, snippet, spans);
    }

    /// <summary>A phone photo of a paper receipt on a wooden table. Decoded like real pictures, so the thumbnail is the real thing.</summary>
    private static Bitmap Photo()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "valafind-docs-" + Guid.NewGuid().ToString("N") + ".png");
        using (var bitmap = new SkiaSharp.SKBitmap(720, 720))
        {
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                using var wood = new SkiaSharp.SKPaint
                {
                    Shader = SkiaSharp.SKShader.CreateLinearGradient(new(0, 0), new(720, 720),
                        [new SkiaSharp.SKColor(0xA8, 0x74, 0x4A), new SkiaSharp.SKColor(0x7A, 0x4E, 0x2C)], SkiaSharp.SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(0, 0, 720, 720, wood);
                using var grain = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x5E, 0x3A, 0x1E, 0x40), StrokeWidth = 3, IsAntialias = true };
                for (var y = 20; y < 720; y += 46)
                    canvas.DrawLine(0, y, 720, y + 30, grain);
                canvas.RotateDegrees(-8, 360, 360);
                using var shadow = new SkiaSharp.SKPaint
                {
                    Color = new SkiaSharp.SKColor(0, 0, 0, 0x55), IsAntialias = true,
                    MaskFilter = SkiaSharp.SKMaskFilter.CreateBlur(SkiaSharp.SKBlurStyle.Normal, 14),
                };
                canvas.DrawRect(214, 84, 300, 580, shadow);
                using var paper = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xFB, 0xF8, 0xF1), IsAntialias = true };
                canvas.DrawRect(200, 70, 300, 580, paper);
                using var ink = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x55, 0x55, 0x5C), IsAntialias = true };
                canvas.DrawRect(250, 110, 200, 26, ink);
                var rnd = new Random(3);
                for (var y = 180; y < 520; y += 34)
                {
                    canvas.DrawRect(232, y, 110 + rnd.Next(0, 60), 12, ink);
                    canvas.DrawRect(420, y, 50, 12, ink);
                }
                canvas.DrawRect(232, 548, 236, 4, ink);
                canvas.DrawRect(232, 572, 120, 20, ink);
                canvas.DrawRect(392, 572, 76, 20, ink);
            }
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        try
        {
            return FilePreviewService.Decode(path, FilePreviewService.ThumbnailSide, fill: true, CancellationToken.None)!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------ composition

    private static Control LauncherView(UiFlavor flavor, LauncherViewModel vm, double scale, double top, double bottom)
    {
        var view = LauncherWindow.CreateView(flavor);
        view.DataContext = vm;
        return new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(scale, scale),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, top, 0, bottom),
            Child = view,
        };
    }

    private static Panel Shot(Control content, IBrush[] backdrop)
    {
        var panel = new Panel { Width = ShotWidth, ClipToBounds = true };
        foreach (var layer in backdrop)
            panel.Children.Add(new Rectangle { Fill = layer });
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>A blurred desktop: a base gradient plus a few soft colour blobs.</summary>
    private static IBrush[] Backdrop(UiFlavor flavor)
    {
        static LinearGradientBrush Linear(string a, string b) => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(a), 0), new GradientStop(Color.Parse(b), 1) },
        };
        static RadialGradientBrush Blob(double x, double y, double radius, string color) => new()
        {
            Center = new RelativePoint(x, y, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(color), 0),
                new GradientStop(Color.Parse("#00" + color[^6..]), 1),
            },
        };
        return flavor switch
        {
            UiFlavor.Windows =>
            [
                Linear("#DCE7F4", "#A9C3E4"),
                Blob(0.12, 0.08, 0.45, "#CCF2F6FB"),
                Blob(0.72, 0.15, 0.40, "#998FA9C9"),
                Blob(0.85, 0.9, 0.50, "#CC9DB9E2"),
            ],
            UiFlavor.Mac =>
            [
                Linear("#D9D6D0", "#C4CDD6"),
                Blob(0.1, 0.1, 0.45, "#CCECE9E3"),
                Blob(0.45, 0.05, 0.35, "#99A8B2BD"),
                Blob(0.9, 0.15, 0.45, "#B3D3DBE2"),
                Blob(0.15, 0.95, 0.5, "#B3E5DDD3"),
            ],
            _ =>
            [
                Linear("#2C557E", "#1B3550"),
                Blob(0.2, 0.1, 0.5, "#663C6C99"),
                Blob(0.85, 0.85, 0.45, "#80142A40"),
            ],
        };
    }

    /// <summary>The cover: logo, tagline and badges on the left, the macOS launcher running off the right edge.</summary>
    private static Control Cover(LauncherViewModel vm)
    {
        var blue = Color.Parse("#0A7CFF");
        var ink = new SolidColorBrush(Color.Parse("#14161A"));
        var font = new FontFamily("fonts:Inter#Inter");
        using var logoStream = AssetLoader.Open(new Uri("avares://Valafind/Assets/valafind.png"));
        var logo = new Bitmap(logoStream);

        Border Badge(string strong, string rest) => new()
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(19),
            Padding = new Thickness(14, 8),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #14000000"),
            Child = new TextBlock
            {
                FontFamily = font,
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                Foreground = ink,
                Inlines =
                {
                    new Avalonia.Controls.Documents.Run(strong) { Foreground = new SolidColorBrush(blue), FontWeight = FontWeight.Bold },
                    new Avalonia.Controls.Documents.Run(rest),
                },
            },
        };

        var left = new StackPanel
        {
            Margin = new Thickness(72, 110, 0, 0),
            Width = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 0,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 24,
                    Children =
                    {
                        new Image { Source = logo, Width = 96, Height = 96 },
                        new TextBlock
                        {
                            Text = "Valafind", FontFamily = font, FontSize = 76, FontWeight = FontWeight.Bold, Foreground = ink,
                            LetterSpacing = -2.5, VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                new TextBlock
                {
                    Margin = new Thickness(0, 34, 0, 0), FontFamily = font, FontSize = 40, FontWeight = FontWeight.Bold, Foreground = ink,
                    LetterSpacing = -1, LineHeight = 46, TextWrapping = TextWrapping.Wrap,
                    Inlines =
                    {
                        new Avalonia.Controls.Documents.Run("Find files by what they "),
                        new Avalonia.Controls.Documents.Run("mean") { Foreground = new SolidColorBrush(blue) },
                        new Avalonia.Controls.Documents.Run(", not what they're called."),
                    },
                },
                new TextBlock
                {
                    Margin = new Thickness(0, 22, 0, 0), FontFamily = font, FontSize = 21, Foreground = new SolidColorBrush(Color.Parse("#5B606A")),
                    LineHeight = 32, TextWrapping = TextWrapping.Wrap,
                    Text = "Semantic search for documents, photos, audio and video that runs entirely on your computer.",
                },
                new StackPanel
                {
                    Margin = new Thickness(0, 26, 0, 0), Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { Badge("100%", " local"), Badge(string.Empty, "Photos, audio, video"), Badge(string.Empty, "Win, Mac, Linux") },
                },
            },
        };

        var view = LauncherWindow.CreateView(UiFlavor.Mac);
        view.DataContext = vm;
        var launcher = new Canvas
        {
            Children = { view },
        };
        Canvas.SetLeft(view, 556);
        Canvas.SetTop(view, 46);

        var cover = new Panel
        {
            Width = 1280,
            Height = 640,
            ClipToBounds = true,
            Children =
            {
                new Rectangle { Fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#FBFBFD"), 0), new GradientStop(Color.Parse("#E6EBF4"), 0.6), new GradientStop(Color.Parse("#C9D6EA"), 1) },
                } },
                launcher,
                left,
            },
        };
        return cover;
    }

    /// <summary>Renders <paramref name="content"/> at its own size times <paramref name="scale"/>. It sits on a canvas in a
    /// small window, so it is not clipped to the headless screen (1920 × 1080). A larger scale goes through a layout
    /// transform rendered at 96 DPI: rendering at a higher DPI drops text and lays nested views out at the wrong size.</summary>
    private static RenderTargetBitmap Capture(Control content, double scale = 1)
    {
        Control root = scale == 1
            ? content
            : new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        var window = new Window
        {
            RequestedThemeVariant = ThemeVariant.Light,
            Width = 200,
            Height = 200,
            Content = new Canvas { Children = { root } },
        };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var size = root.Bounds.Size;
            var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Round(size.Width), (int)Math.Round(size.Height)));
            bitmap.Render(root);
            return bitmap;
        }
        finally
        {
            window.Close();
        }
    }

    private static void Save(Bitmap frame, string name)
    {
        var dir = Environment.GetEnvironmentVariable(DirVariable);
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        using var file = File.Create(System.IO.Path.Combine(dir, name));
        frame.Save(file, new PngBitmapEncoderOptions());
    }
}
