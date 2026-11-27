// ail-derived-from: NameOnly@ec098ee66742baa3
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class NameOnlyTests(HeadlessFixture fixture)
{
    [Fact(DisplayName = "an archive is found by its name, not by what is in it")]
    public Task AnArchiveIsFoundByItsNameNotByWhatIsInIt() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var otherCode = AppHarness.Unique();
        var name = $"holiday-photos-from-crete-{code}.zip";

        app.SaveBytes(name, ZipHolding("whale.txt", $"The submarine sonar heard a whale song {otherCode}."));

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("holiday photos from Crete", () => app.ResultNames.Contains(name));

        Assert.Contains(name, app.ResultNames);
        AssertNotShownAnywhere(app, otherCode);

        AppHarness.SaveScreen(app.LauncherWindow, "an-archive-is-found-by-its-name-not-by-what-is-in-it.png");
    });

    [Fact(DisplayName = "an old Word file is found by its name only")]
    public Task AnOldWordFileIsFoundByItsNameOnly() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var otherCode = AppHarness.Unique();
        var name = $"minutes-of-the-board-meeting-{code}.doc";

        app.SaveText(name, $"The elephant parade {otherCode} passed the castle gates.");

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("board meeting minutes", () => app.ResultNames.Contains(name));

        Assert.Contains(name, app.ResultNames);
        AssertNotShownAnywhere(app, otherCode);

        AppHarness.SaveScreen(app.LauncherWindow, "an-old-word-file-is-found-by-its-name-only.png");
    });

    [Fact(DisplayName = "a video no decoder can read is still found by its name")]
    public Task AVideoNoDecoderCanReadIsStillFoundByItsName() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var name = $"sailing-trip-to-norway-{code}.mp4";

        app.SaveRandomBytes(name, 4096);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync("sailing trip to Norway", () => app.ResultNames.Contains(name));

        Assert.Contains(name, app.ResultNames);

        AppHarness.SaveScreen(app.LauncherWindow, "a-video-no-decoder-can-read-is-still-found-by-its-name.png");
    });

    private static byte[] ZipHolding(string entryName, string text)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }
        return buffer.ToArray();
    }

    private static void AssertNotShownAnywhere(dynamic app, string code)
    {
        var texts = ((System.Collections.Generic.IEnumerable<string>)AppHarness.Texts(app.LauncherWindow)).ToList();
        Assert.DoesNotContain(texts, t => t.Contains(code, StringComparison.Ordinal));
    }
}
