// ail-derived-from: TextSniffing@9e6c9f9936d358b8
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class TextSniffingTests(HeadlessFixture fixture)
{
    [Fact(DisplayName = "a text file without an extension is read")]
    public Task ATextFileWithoutAnExtensionIsRead() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var otherCode = AppHarness.Unique();
        var name = $"{code}-CHANGELOG";
        app.SaveText(
            name,
            $"Release {otherCode}: fixed the crash when the printer ran out of paper during a long print job.",
            DateTime.Now);

        await app.StartAsync();
        await app.SearchUntilAsync("the printer ran out of paper", () => app.ResultNames.Contains(name));

        Assert.Contains(name, app.ResultNames);
        var shown = string.Concat(AppHarness.Texts(app.LauncherWindow));
        Assert.Contains(otherCode, shown);

        AppHarness.SaveScreen(app.LauncherWindow, "a-text-file-without-an-extension-is-read.png");
    });

    [Fact(DisplayName = "a UTF-16 text file without a byte order mark is read")]
    public Task AUtf16TextFileWithoutAByteOrderMarkIsRead() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var otherCode = AppHarness.Unique();
        var name = $"greenhouse-{code}.log";
        var text = $"Night {otherCode}: the greenhouse heater switched on because frost was coming.";
        // Encoding.Unicode.GetBytes writes UTF-16 little-endian without a byte order mark.
        app.SaveBytes(name, Encoding.Unicode.GetBytes(text));

        await app.StartAsync();
        await app.SearchUntilAsync("frost in the greenhouse", () => app.ResultNames.Contains(name));

        Assert.Contains(name, app.ResultNames);
        var shown = string.Concat(AppHarness.Texts(app.LauncherWindow));
        Assert.Contains(otherCode, shown);

        AppHarness.SaveScreen(app.LauncherWindow, "a-utf-16-text-file-without-a-byte-order-mark-is-read.png");
    });

    [Fact(DisplayName = "a binary file with an unknown extension is not indexed")]
    public Task ABinaryFileWithAnUnknownExtensionIsNotIndexed() => AppHarness.RunAsync(fixture, async app =>
    {
        var code = AppHarness.Unique();
        var binaryName = $"sailing-logbook-{code}.bin";
        var textName = $"sailing-notes-{code}.txt";
        app.SaveRandomBytes(binaryName, 8192);
        app.SaveText(
            textName,
            "Notes from the sailing trip: we reefed the mainsail when the wind picked up past the harbour.",
            DateTime.Now);

        await app.StartAsync();
        await app.SearchUntilAsync("sailing logbook", () => app.ResultNames.Contains(textName));

        Assert.Contains(textName, app.ResultNames);
        Assert.DoesNotContain(binaryName, app.ResultNames);

        AppHarness.SaveScreen(app.LauncherWindow, "a-binary-file-with-an-unknown-extension-is-not-indexed.png");
    });
}
