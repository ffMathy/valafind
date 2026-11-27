// ail-derived-from: Video@d38351e57209fb68
using System.Collections.Generic;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class VideoTests(HeadlessFixture fixture)
{
    [Fact(DisplayName = "a video is found by what its pictures show")]
    public Task AVideoIsFoundByWhatItsPicturesShow() => AppHarness.RunAsync(fixture, async app =>
    {
        var red = $"{AppHarness.Unique()}.mp4";
        var blue = $"{AppHarness.Unique()}.mp4";

        // A 3-second video of a red circle on white, without sound.
        app.SaveWithFfmpeg(red,
            "-f lavfi -i color=c=white:s=640x480:d=3:r=10 " +
            "-vf format=gbrp,geq=r=255:g=if(lt(hypot(X-320\\,Y-240)\\,160)\\,0\\,255):b=if(lt(hypot(X-320\\,Y-240)\\,160)\\,0\\,255) " +
            "-an -pix_fmt yuv420p");

        // A 3-second video of a blue square on white, without sound.
        app.SaveWithFfmpeg(blue,
            "-f lavfi -i color=c=white:s=640x480:d=3:r=10 " +
            "-vf drawbox=x=170:y=90:w=300:h=300:color=blue:t=fill " +
            "-an -pix_fmt yuv420p");

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("a red circle",
            () => IndexOf(app.ResultNames, red) >= 0 && IndexOf(app.ResultNames, blue) >= 0);

        var redIndex = IndexOf(app.ResultNames, red);
        var blueIndex = IndexOf(app.ResultNames, blue);
        Assert.True(redIndex >= 0, $"{red} is not listed");
        Assert.True(blueIndex >= 0, $"{blue} is not listed");
        Assert.True(redIndex < blueIndex,
            $"the red circle video ({redIndex}) is not listed above the blue square video ({blueIndex})");

        AppHarness.SaveScreen(app.LauncherWindow, "a-video-is-found-by-what-its-pictures-show.png");
    });

    [Fact(DisplayName = "a video is found by what is said in it")]
    public Task AVideoIsFoundByWhatIsSaidInIt() => AppHarness.RunAsync(fixture, async app =>
    {
        var forecast = $"{AppHarness.Unique()}.mp4";
        var tone = $"{AppHarness.Unique()}.mp4";

        // A black video in which a weather forecast is read out.
        app.SaveWithFfmpeg(forecast,
            "-f lavfi -i color=c=black:s=640x480:r=10 " +
            "-i fixture:speech.wav " +
            "-shortest -pix_fmt yuv420p");

        // A black video with a steady tone.
        app.SaveWithFfmpeg(tone,
            "-f lavfi -i color=c=black:s=640x480:d=5:r=10 " +
            "-f lavfi -i sine=frequency=440:duration=5 " +
            "-shortest -pix_fmt yuv420p");

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();

        await app.SearchUntilAsync("a weather forecast",
            () => IndexOf(app.ResultNames, forecast) >= 0 && IndexOf(app.ResultNames, tone) >= 0);

        var forecastIndex = IndexOf(app.ResultNames, forecast);
        var toneIndex = IndexOf(app.ResultNames, tone);
        Assert.True(forecastIndex >= 0, $"{forecast} is not listed");
        Assert.True(toneIndex >= 0, $"{tone} is not listed");
        Assert.True(forecastIndex < toneIndex,
            $"the forecast video ({forecastIndex}) is not listed above the tone video ({toneIndex})");

        AppHarness.SaveScreen(app.LauncherWindow, "a-video-is-found-by-what-is-said-in-it.png");
    });

    private static int IndexOf(IEnumerable<string> names, string name)
    {
        var i = 0;
        foreach (var n in names)
        {
            if (n == name)
            {
                return i;
            }
            i++;
        }
        return -1;
    }
}
