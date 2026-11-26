using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using SkiaSharp;
using Avalonia.LogicalTree;
using Valafind.App.Localization;
using Valafind.App.Services;
using Valafind.App.ViewModels;
using Valafind.App.Views;
using Valafind.Core.Abstractions;
using Valafind.Core.Configuration;
using Valafind.Core.Indexing;
using Xunit;
using static Valafind.App.Tests.Headless.DocsScreenshotTests;

namespace Valafind.App.Tests.Headless;

/// <summary>
/// Renders the Microsoft Store screenshots (1920 × 1080) and logos, one set per UI language: a headline over the real
/// Windows launcher (or settings window), with the UI, the headline and the query in that language. The texts come from
/// <c>packaging/store/listing/text/&lt;language&gt;.json</c>. Without <c>VALAFIND_STORE_LISTING_DIR</c> only English
/// renders (to catch a broken layout) and nothing is written; with it, every language with a text file is written to
/// <c>&lt;dir&gt;/&lt;language&gt;/</c>, or only those in <c>VALAFIND_STORE_LANGUAGES</c> (comma separated).
/// <c>packaging/store/listing/make-listing.sh</c> runs this and packs the result for Partner Center.
/// </summary>
[Collection(HeadlessUiDefinition.Name)]
public sealed class StoreListingScreenshotTests(HeadlessFixture fixture)
{
    private const string DirVariable = "VALAFIND_STORE_LISTING_DIR";
    private const string LanguagesVariable = "VALAFIND_STORE_LANGUAGES";
    private const int Width = 1920;
    private const int Height = 1080;

    private static readonly string[] Scenes = ["search", "media", "photos", "recordings", "indexing", "settings"];
    private static readonly Color Blue = Color.Parse("#0A7CFF");
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#14161A"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#4A5160"));

    [Fact]
    public Task RendersStoreScreenshots() =>
        fixture.Session.Dispatch(() =>
        {
            var output = Environment.GetEnvironmentVariable(DirVariable);
            var languages = Languages(string.IsNullOrWhiteSpace(output));
            Assert.NotEmpty(languages);
            var previous = (System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CultureInfo.CurrentCulture);
            try
            {
                foreach (var language in languages)
                {
                    var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
                    System.Globalization.CultureInfo.CurrentUICulture = culture;
                    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.CreateSpecificCulture(culture.Name);
                    using var text = JsonDocument.Parse(File.ReadAllText(TextFile(language)));
                    var shots = text.RootElement.GetProperty("screenshots");
                    for (var i = 0; i < Scenes.Length; i++)
                    {
                        var shot = shots.GetProperty(Scenes[i]);
                        var frame = Capture(Compose(Scenes[i], shot, shots.GetProperty("search").GetProperty("query").GetString()!));
                        Assert.Equal(new PixelSize(Width, Height), frame.PixelSize);
                        Save(frame, output, language, "screenshots", $"{i + 1:00}-{Scenes[i]}.png");
                    }
                }
            }
            finally
            {
                (System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CultureInfo.CurrentCulture) = previous;
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RendersStoreLogos() =>
        fixture.Session.Dispatch(() =>
        {
            var output = Environment.GetEnvironmentVariable(DirVariable);
            foreach (var (name, width, height) in new[]
            {
                ("poster-1440x2160.png", 1440, 2160), ("boxart-2160x2160.png", 2160, 2160), ("tile-300x300.png", 300, 300),
            })
            {
                using var bitmap = Logo(width, height, tile: name.StartsWith("tile", StringComparison.Ordinal));
                Assert.Equal(width, bitmap.Width);
                if (string.IsNullOrWhiteSpace(output))
                    continue;
                var dir = System.IO.Path.Combine(output, "logos");
                Directory.CreateDirectory(dir);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(System.IO.Path.Combine(dir, name), data.ToArray());
            }
        }, TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ languages and texts

    private static List<string> Languages(bool englishOnly)
    {
        if (englishOnly)
            return ["en"];
        var wanted = Environment.GetEnvironmentVariable(LanguagesVariable);
        var codes = string.IsNullOrWhiteSpace(wanted)
            ? Loc.Languages.Select(l => l.Code)
            : wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return codes.Where(c => File.Exists(TextFile(c))).ToList();
    }

    private static string TextFile(string language)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Valafind.slnx")))
                return System.IO.Path.Combine(dir.FullName, "packaging", "store", "listing", "text", language + ".json");
        }
        throw new InvalidOperationException("Valafind.slnx not found above " + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------------ composition

    private static Control Compose(string scene, JsonElement shot, string searchQuery)
    {
        var query = shot.TryGetProperty("query", out var q) ? q.GetString()! : null;
        var ui = scene switch
        {
            "search" => View(Launcher(UiFlavor.Windows, query!)),
            "media" => View(MediaLauncher(UiFlavor.Windows, query!)),
            "photos" => View(PhotoSearch(query!)),
            "recordings" => View(RecordingSearch(query!)),
            "indexing" => View(Indexing(searchQuery)),
            "settings" => Settings(),
            _ => throw new ArgumentOutOfRangeException(nameof(scene)),
        };

        var font = new FontFamily("fonts:Inter#Inter");
        var heading = new StackPanel
        {
            Margin = new Thickness(120, 58, 120, 0),
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = shot.GetProperty("headline").GetString(), FontFamily = font, FontSize = 58, FontWeight = FontWeight.Bold,
                    Foreground = Ink, LetterSpacing = -1.2, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = shot.GetProperty("subline").GetString(), FontFamily = font, FontSize = 25, LineHeight = 36, Foreground = Muted,
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 1320,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(heading);
        var box = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Margin = new Thickness(160, 34, 160, 40),
            VerticalAlignment = VerticalAlignment.Top,
            Child = ui,
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var panel = new Panel { Width = Width, Height = Height, ClipToBounds = true, FlowDirection = Loc.FlowDirection };
        foreach (var layer in Backdrop(UiFlavor.Windows))
            panel.Children.Add(new Rectangle { Fill = layer });
        panel.Children.Add(grid);
        return panel;
    }

    private static Control View(LauncherViewModel vm)
    {
        var view = LauncherWindow.CreateView(UiFlavor.Windows);
        view.DataContext = vm;
        view.FlowDirection = Loc.FlowDirection;
        return view;
    }

    private static LauncherViewModel NewLauncher() =>
        new(UiFlavor.Windows, new ValafindSettings(), new FixedTime(), static a => a(), Home, Home + @"\AppData\Local\Programs\Valafind");

    private const string Home = @"C:\Users\alex";

    private static void UpToDate(LauncherViewModel vm) =>
        vm.UpdateStatus(DesignData.IdleStatus with { State = IndexingState.UpToDate, AllDone = 189_144, AllTotal = 189_144 });

    /// <summary>Pictures found by what is in them: the photo of the red car in the snow first, then near misses.</summary>
    private static LauncherViewModel PhotoSearch(string query)
    {
        var camera = Home + @"\Pictures\Camera Roll";
        var trip = Home + @"\Pictures\2025\Ski trip";
        var scenes = new (string Dir, string Name, float Score, Action<SKCanvas> Draw)[]
        {
            (trip, "IMG_20250118_103412.jpg", 0.61f, c => Snow(c, car: true)),
            (camera, "PXL_20241203_081530.jpg", 0.52f, c => Snow(c, car: true, night: true)),
            (trip, "IMG_20250117_154001.jpg", 0.44f, c => Snow(c, car: false)),
            (Home + @"\OneDrive\Pictures", "DSC04211.jpg", 0.39f, c => Summer(c)),
            (camera, "PXL_20240611_183302.jpg", 0.31f, c => Snow(c, car: false, night: true)),
        };
        var vm = NewLauncher();
        vm.LoadSnapshot(query, scenes.Select(s => Hit(s.Dir, s.Name, FileKind.Image, 3_480_112, s.Score, string.Empty)).ToArray(),
            TimeSpan.FromMilliseconds(287));
        UpToDate(vm);
        for (var i = 0; i < scenes.Length; i++)
        {
            var item = vm.Results[i];
            item.Thumbnail = Picture(scenes[i].Draw, FilePreviewService.ThumbnailSide, fill: true);
            if (i == 0)
                item.ApplyPreview(new FilePreview(Picture(scenes[i].Draw, 640, fill: false), null, false));
        }
        return vm;
    }

    /// <summary>Recordings and videos found by what was said in them, with the meeting notes next to them.</summary>
    private static LauncherViewModel RecordingSearch(string query)
    {
        var rec = Home + @"\Documents\Sound recordings";
        var vids = Home + @"\Videos\Meetings";
        SearchResult[] results =
        [
            Hit(rec, "Recording 2025-03-04 10.02.m4a", FileKind.Audio, 48_211_003, 0.57f, string.Empty),
            Hit(vids, "Team sync 2025-03-11.mp4", FileKind.Video, 412_700_118, 0.49f, string.Empty),
            Hit(Home + @"\Documents\Work", "Budget review — notes.md", FileKind.Text, 2_311, 0.45f,
                "# Budget review, 4 March We agreed to move the marketing budget to Q2 and keep travel at last year's level…",
                "move the marketing budget to Q2"),
            Hit(rec, "Voice memo 14.m4a", FileKind.Audio, 2_004_552, 0.38f, string.Empty),
            Hit(Home + @"\Documents\Finance", "Budget 2025.xlsx", FileKind.Spreadsheet, 38_220, 0.33f,
                "Department,Q1,Q2,Q3,Q4 Marketing,120000,180000,150000,150000 Travel,40000,40000,30000,45000…", "Marketing"),
            Hit(vids, "All hands February.mp4", FileKind.Video, 1_204_993_210, 0.27f, string.Empty),
        ];
        var vm = NewLauncher();
        vm.LoadSnapshot(query, results, TimeSpan.FromMilliseconds(304));
        UpToDate(vm);
        return vm;
    }

    /// <summary>The deposit search while the index is still being built, with the load meters showing.</summary>
    private static LauncherViewModel Indexing(string query)
    {
        var vm = Launcher(UiFlavor.Windows, query);
        vm.UpdateStatus(DesignData.BusyStatus);
        return vm;
    }

    /// <summary>The settings window twice, side by side: the shortcut and folders, and the limits and privacy further
    /// down. Its content is moved out of the window so it renders as vectors at the screenshot's scale.</summary>
    private static Control Settings() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 48,
        Children = { SettingsPane(limits: false), SettingsPane(limits: true) },
    };

    private static Control SettingsPane(bool limits)
    {
        var settings = new ValafindSettings
        {
            Roots = [Home + @"\Documents", Home + @"\Pictures", Home + @"\Music", Home + @"\Videos", @"D:\Projects"],
        };
        var vm = new SettingsViewModel(settings, new StoreHost(), macOS: false, Home);
        var window = new SettingsWindow(vm);
        var content = (Control)window.Content!;
        window.Content = null;
        var host = new Border
        {
            Width = 580,
            Height = 760,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#F3F3F3")),
            BorderBrush = new SolidColorBrush(Color.Parse("#1F000000")),
            BorderThickness = new Thickness(1),
            DataContext = vm,
            FlowDirection = Loc.FlowDirection,
            Child = content,
        };
        var resources = window.Resources;
        window.Resources = new Avalonia.Controls.ResourceDictionary();
        host.Resources = resources;
        foreach (var style in window.Styles.ToList())
        {
            window.Styles.Remove(style);
            host.Styles.Add(style);
        }

        if (limits)
        {
            // Start the page at the resource limits: drop the sections above them.
            var page = (StackPanel)content.GetLogicalDescendants().OfType<ScrollViewer>().First().Content!;
            var at = page.Children.ToList().FindIndex(c => c.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Text == Strings.SettingsIdleSection));
            Assert.True(at > 0);
            page.Children.RemoveRange(0, at);
        }
        return host;
    }

    /// <summary>The settings window as the Store copy shows it: the model is built in, and Windows runs the startup task.</summary>
    private sealed class StoreHost : ISettingsHost
    {
        public string? DetectModel() => @"C:\Program Files\WindowsApps\Valafind\models\embeddinggemma-2";
        public string DefaultModelDirectory => DetectModel()!;
        public string AppDirectory => Home + @"\AppData\Local\Programs\Valafind";
        public string LogDirectory => Home + @"\AppData\Local\Valafind\logs";
        public void OpenLogFolder() { }
        public bool IsAutostartEnabled() => true;
        public void SetAutostartEnabled(bool enabled) { }
        public bool IsAutostartManagedBySystem => true;
        public void RestartToUpdate() { }
        public Task<string?> ApplyAsync(SettingsSnapshot before, ValafindSettings after) => Task.FromResult<string?>(null);
    }

    private static RenderTargetBitmap Capture(Control content) => DocsScreenshotTests.Capture(content);

    private static void Save(Bitmap frame, string? output, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;
        var path = System.IO.Path.Combine([output, .. parts]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        frame.Save(file, new PngBitmapEncoderOptions());
    }

    // ------------------------------------------------------------------ pictures

    /// <summary>Draws a 4:3 photo and decodes it like a real picture on disk, so thumbnails and previews are the real thing.</summary>
    internal static Bitmap Picture(Action<SKCanvas> draw, int side, bool fill)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "valafind-store-" + Guid.NewGuid().ToString("N") + ".png");
        using (var bitmap = new SKBitmap(1200, 900))
        {
            using (var canvas = new SKCanvas(bitmap))
                draw(canvas);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        try
        {
            return FilePreviewService.Decode(path, side, fill, CancellationToken.None)!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true };

    private static void Gradient(SKCanvas c, float top, float bottom, SKColor a, SKColor b)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom), [a, b], SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, top, 1200, bottom - top, paint);
    }

    private static void Mountains(SKCanvas c, SKColor rock, SKColor snow, float baseline)
    {
        using var path = new SKPath();
        path.MoveTo(0, baseline);
        path.LineTo(180, 300);
        path.LineTo(330, 420);
        path.LineTo(560, 210);
        path.LineTo(760, 400);
        path.LineTo(930, 260);
        path.LineTo(1200, 440);
        path.LineTo(1200, baseline);
        path.Close();
        using var rockPaint = Fill(rock);
        c.DrawPath(path, rockPaint);
        using var caps = new SKPath();
        caps.MoveTo(140, 340); caps.LineTo(180, 300); caps.LineTo(225, 340); caps.Close();
        caps.MoveTo(490, 275); caps.LineTo(560, 210); caps.LineTo(630, 275); caps.LineTo(560, 255); caps.Close();
        caps.MoveTo(880, 300); caps.LineTo(930, 260); caps.LineTo(985, 300); caps.Close();
        using var snowPaint = Fill(snow);
        c.DrawPath(caps, snowPaint);
    }

    private static void Snow(SKCanvas c, bool car, bool night = false)
    {
        if (night)
            Gradient(c, 0, 620, new SKColor(0x2B, 0x3A, 0x5C), new SKColor(0x6F, 0x82, 0xA6));
        else
            Gradient(c, 0, 620, new SKColor(0x9C, 0xC4, 0xEC), new SKColor(0xE4, 0xEE, 0xF8));
        Mountains(c, night ? new SKColor(0x4A, 0x58, 0x74) : new SKColor(0x8E, 0xA3, 0xBC), SKColors.White, 640);
        Gradient(c, 600, 900, night ? new SKColor(0xC8, 0xD2, 0xE4) : new SKColor(0xF7, 0xFA, 0xFD),
            night ? new SKColor(0xA4, 0xB2, 0xCA) : new SKColor(0xDB, 0xE6, 0xF1));
        using var tree = Fill(night ? new SKColor(0x1F, 0x3A, 0x34) : new SKColor(0x2E, 0x5D, 0x4B));
        foreach (var (x, h) in new[] { (90f, 230f), (170f, 180f), (1040f, 250f), (1120f, 190f) })
        {
            using var t = new SKPath();
            t.MoveTo(x, 640 - h); t.LineTo(x - h * 0.33f, 640); t.LineTo(x + h * 0.33f, 640); t.Close();
            c.DrawPath(t, tree);
        }
        if (car)
            Car(c, 330, 520, new SKColor(0xD3, 0x1F, 0x26), night);
        using var flake = Fill(new SKColor(0xFF, 0xFF, 0xFF, 0xD0));
        var rnd = new Random(night ? 7 : 11);
        for (var i = 0; i < 160; i++)
            c.DrawCircle(rnd.Next(0, 1200), rnd.Next(0, 900), rnd.Next(2, 6), flake);
    }

    private static void Summer(SKCanvas c)
    {
        Gradient(c, 0, 600, new SKColor(0x4C, 0x9B, 0xE8), new SKColor(0xBF, 0xE1, 0xF8));
        Mountains(c, new SKColor(0x5E, 0x8C, 0x5A), new SKColor(0x7F, 0xAE, 0x72), 640);
        Gradient(c, 600, 900, new SKColor(0x6D, 0x6F, 0x73), new SKColor(0x4D, 0x4F, 0x53));
        using var line = Fill(new SKColor(0xF2, 0xD4, 0x4C));
        for (var x = 0; x < 1200; x += 160)
            c.DrawRect(x, 780, 90, 12, line);
        Car(c, 360, 520, new SKColor(0xD3, 0x1F, 0x26), false);
        using var sun = Fill(new SKColor(0xFF, 0xF1, 0xB8));
        c.DrawCircle(1040, 120, 60, sun);
    }

    private static void Car(SKCanvas c, float x, float y, SKColor body, bool lightsOn)
    {
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0x40), IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12),
        };
        c.DrawOval(x + 270, y + 225, 300, 34, shadow);
        using var paint = Fill(body);
        using var cabin = new SKPath();
        cabin.MoveTo(x + 120, y + 90);
        cabin.CubicTo(x + 170, y + 10, x + 210, y, x + 290, y);
        cabin.LineTo(x + 360, y);
        cabin.CubicTo(x + 410, y, x + 440, y + 30, x + 470, y + 90);
        cabin.Close();
        c.DrawPath(cabin, paint);
        c.DrawRoundRect(new SKRoundRect(new SKRect(x, y + 80, x + 560, y + 200), 46, 46), paint);
        using var glass = Fill(lightsOn ? new SKColor(0x30, 0x3C, 0x52) : new SKColor(0xBF, 0xD7, 0xEA));
        using var w1 = new SKPath();
        w1.MoveTo(x + 160, y + 88); w1.CubicTo(x + 195, y + 30, x + 225, y + 22, x + 280, y + 22);
        w1.LineTo(x + 290, y + 88); w1.Close();
        c.DrawPath(w1, glass);
        using var w2 = new SKPath();
        w2.MoveTo(x + 310, y + 22); w2.LineTo(x + 355, y + 22); w2.CubicTo(x + 395, y + 22, x + 420, y + 45, x + 440, y + 88);
        w2.LineTo(x + 310, y + 88); w2.Close();
        c.DrawPath(w2, glass);
        using var tyre = Fill(new SKColor(0x22, 0x22, 0x26));
        using var rim = Fill(new SKColor(0xB8, 0xBE, 0xC6));
        foreach (var cx in new[] { x + 130, x + 440 })
        {
            c.DrawCircle(cx, y + 200, 52, tyre);
            c.DrawCircle(cx, y + 200, 24, rim);
        }
        using var lamp = Fill(lightsOn ? new SKColor(0xFF, 0xF4, 0xC2) : new SKColor(0xF5, 0xE7, 0xB0));
        c.DrawRoundRect(new SKRoundRect(new SKRect(x + 528, y + 108, x + 556, y + 132), 6, 6), lamp);
        if (lightsOn)
        {
            using var beam = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(x + 556, y + 120), new SKPoint(x + 860, y + 160),
                    [new SKColor(0xFF, 0xF4, 0xC2, 0x90), new SKColor(0xFF, 0xF4, 0xC2, 0x00)], SKShaderTileMode.Clamp),
                IsAntialias = true,
            };
            using var cone = new SKPath();
            cone.MoveTo(x + 556, y + 112); cone.LineTo(x + 860, y + 60); cone.LineTo(x + 860, y + 240); cone.LineTo(x + 556, y + 130); cone.Close();
            c.DrawPath(cone, beam);
        }
    }

    // ------------------------------------------------------------------ logos

    /// <summary>
    /// The Valafind icon, drawn as vectors so it is sharp at any size: a blue rounded square with a white magnifier
    /// holding a plus. A tile is the icon alone on a transparent background; poster and box art put it, larger, on the
    /// brand gradient.
    /// </summary>
    internal static SKBitmap Logo(int width, int height, bool tile)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bitmap);
        c.Clear(SKColors.Transparent);
        float side;
        SKPoint origin;
        if (tile)
        {
            side = Math.Min(width, height) * 0.84f;
            origin = new SKPoint((width - side) / 2, (height - side) / 2);
        }
        else
        {
            using var background = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                    [new SKColor(0xF4, 0xF8, 0xFE), new SKColor(0xC9, 0xDB, 0xF3)], SKShaderTileMode.Clamp),
            };
            c.DrawRect(0, 0, width, height, background);
            side = Math.Min(width, height) * 0.46f;
            origin = new SKPoint((width - side) / 2, (height - side) / 2 - (height > width ? height * 0.04f : 0));
            using var glow = new SKPaint
            {
                Color = new SKColor(0x0A, 0x7C, 0xFF, 0x40), IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, side * 0.08f),
            };
            c.DrawRoundRect(new SKRoundRect(new SKRect(origin.X, origin.Y + side * 0.05f, origin.X + side, origin.Y + side * 1.05f),
                side * 0.24f, side * 0.24f), glow);
        }

        Icon(c, origin.X, origin.Y, side);

        if (!tile)
        {
            using var typeface = SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold) ?? SKTypeface.Default;
            using var font = new SKFont(typeface, side * 0.27f);
            using var ink = Fill(new SKColor(0x14, 0x16, 0x1A));
            var y = origin.Y + side + side * 0.42f;
            c.DrawText("Valafind", width / 2f, y, SKTextAlign.Center, font, ink);
        }
        return bitmap;
    }

    private static void Icon(SKCanvas c, float x, float y, float side)
    {
        var rect = new SKRect(x, y, x + side, y + side);
        using var square = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(x, y), new SKPoint(x + side, y + side),
                [new SKColor(0x2E, 0x8B, 0xF2), new SKColor(0x0A, 0x5C, 0xC6)], SKShaderTileMode.Clamp),
        };
        c.DrawRoundRect(new SKRoundRect(rect, side * 0.22f, side * 0.22f), square);

        using var white = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = side * 0.062f,
            StrokeCap = SKStrokeCap.Round,
        };
        var cx = x + side * 0.405f;
        var cy = y + side * 0.40f;
        var r = side * 0.205f;
        c.DrawCircle(cx, cy, r, white);
        var d = (float)(r / Math.Sqrt(2));
        c.DrawLine(cx + d, cy + d, x + side * 0.79f, y + side * 0.785f, white);
        white.StrokeWidth = side * 0.078f;
        var arm = side * 0.048f;
        c.DrawLine(cx - arm, cy, cx + arm, cy, white);
        c.DrawLine(cx, cy - arm, cx, cy + arm, white);
    }
}
