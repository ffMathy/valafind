// ail-derived-from: Pictures@47a3987c5577b814
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class PicturesTests(HeadlessFixture fixture)
{
    private static int IndexOf(IEnumerable<string> names, string name) =>
        names.ToList().FindIndex(n => string.Equals(n, name, StringComparison.Ordinal));

    // "upper is listed above lower": the upper file is listed, and the lower one is either not listed or listed after it.
    private static bool IsAbove(IEnumerable<string> names, string upper, string lower)
    {
        var list = names.ToList();
        var u = IndexOf(list, upper);
        var l = IndexOf(list, lower);
        return u >= 0 && (l < 0 || u < l);
    }

    private static void AssertAbove(IEnumerable<string> names, string upper, string lower)
    {
        var list = names.ToList();
        var u = IndexOf(list, upper);
        var l = IndexOf(list, lower);
        Assert.True(u >= 0, $"Expected \"{upper}\" to be listed, got: [{string.Join(", ", list)}]");
        Assert.True(l < 0 || u < l, $"Expected \"{upper}\" above \"{lower}\", got: [{string.Join(", ", list)}]");
    }

    [Fact(DisplayName = "a picture is found by what it shows")]
    public Task APictureIsFoundByWhatItShows() => AppHarness.RunAsync(fixture, async app =>
    {
        var circle = $"{AppHarness.Unique()}.png";
        var square = $"{AppHarness.Unique()}.png";
        app.SavePicture(circle, Picture.RedCircle);
        app.SavePicture(square, Picture.BlueSquare);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("a red circle", () => IsAbove(app.ResultNames, circle, square));
        AssertAbove(app.ResultNames, circle, square);

        await app.SearchUntilAsync("a blue square", () => IsAbove(app.ResultNames, square, circle));
        AssertAbove(app.ResultNames, square, circle);

        AppHarness.SaveScreen(app.LauncherWindow!, "a-picture-is-found-by-what-it-shows.png");
    });

    [Fact(DisplayName = "a picture is found by the words written in it")]
    public Task APictureIsFoundByTheWordsWrittenInIt() => AppHarness.RunAsync(fixture, async app =>
    {
        var banana = $"{AppHarness.Unique()}.png";
        var tractor = $"{AppHarness.Unique()}.png";
        app.SavePicture(banana, Picture.Word, "BANANA");
        app.SavePicture(tractor, Picture.Word, "TRACTOR");

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("a sign that says banana", () => IsAbove(app.ResultNames, banana, tractor));
        AssertAbove(app.ResultNames, banana, tractor);

        AppHarness.SaveScreen(app.LauncherWindow!, "a-picture-is-found-by-the-words-written-in-it.png");
    });

    [Fact(DisplayName = "PNG, JPEG, GIF, BMP and WebP pictures are all read")]
    public Task PngJpegGifBmpAndWebpPicturesAreAllRead() => AppHarness.RunAsync(fixture, async app =>
    {
        var circles = new[] { "png", "jpg", "gif", "bmp", "webp" }
            .Select(ext => $"{AppHarness.Unique()}.{ext}")
            .ToArray();
        foreach (var circle in circles)
        {
            app.SavePicture(circle, Picture.RedCircle);
        }
        var square = $"{AppHarness.Unique()}.png";
        app.SavePicture(square, Picture.BlueSquare);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("a red circle",
            () => circles.All(c => IsAbove(app.ResultNames, c, square)));
        foreach (var circle in circles)
        {
            AssertAbove(app.ResultNames, circle, square);
        }

        AppHarness.SaveScreen(app.LauncherWindow!, "png-jpeg-gif-bmp-and-webp-pictures-are-all-read.png");
    });

    [Fact(DisplayName = "a TIFF picture is found by its name only, except on a Mac")]
    public Task ATiffPictureIsFoundByItsNameOnlyExceptOnAMac() => AppHarness.RunAsync(fixture, async app =>
    {
        var tiff = $"lighthouse-{AppHarness.Unique()}.tiff";
        var png = $"{AppHarness.Unique()}.png";
        app.SavePicture(tiff, Picture.RedCircle);
        app.SavePicture(png, Picture.RedCircle);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("lighthouse", () => IndexOf(app.ResultNames, tiff) >= 0);
        Assert.Contains(tiff, app.ResultNames);

        await app.SearchUntilAsync("a red circle", () => IsAbove(app.ResultNames, png, tiff));
        AssertAbove(app.ResultNames, png, tiff);

        AppHarness.SaveScreen(app.LauncherWindow!, "a-tiff-picture-is-found-by-its-name-only-except-on-a-mac.png");
    });
}
