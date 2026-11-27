// ail-derived-from: Audio@a36f769b51c4a86d
using System.Linq;
using System.Threading.Tasks;
using Valafind.App.Tests.Headless;
using Xunit;

namespace Valafind.App.Tests.Ail;

[Collection(HeadlessUiDefinition.Name)]
public sealed class AudioTests(HeadlessFixture fixture)
{
    private const string Query = "a weather forecast";

    private const string ToneArguments = "-f lavfi -i sine=frequency=440:duration=20 -ac 1 -ar 16000";

    private static void AssertListedAbove(System.Collections.Generic.IReadOnlyList<string> names, string above, string below)
    {
        var list = names.ToList();
        var aboveIndex = list.IndexOf(above);
        Assert.True(aboveIndex >= 0, $"\"{above}\" is not listed; results: {string.Join(", ", list)}");
        var belowIndex = list.IndexOf(below);
        if (belowIndex >= 0)
        {
            Assert.True(aboveIndex < belowIndex,
                $"\"{above}\" (at {aboveIndex}) is not listed above \"{below}\" (at {belowIndex}); results: {string.Join(", ", list)}");
        }
    }

    [Fact(DisplayName = "a recording is found by what is said in it")]
    public Task ARecordingIsFoundByWhatIsSaidInIt() => AppHarness.RunAsync(fixture, async app =>
    {
        var forecast = $"{AppHarness.Unique()}.wav";
        var tone = $"{AppHarness.Unique()}.wav";
        app.SaveFixture("speech.wav", $"Recordings/{forecast}");
        app.SaveWithFfmpeg($"Recordings/{tone}", ToneArguments);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(Query, () => app.ResultNames.Contains(forecast));

        AssertListedAbove(app.ResultNames.ToList(), forecast, tone);

        AppHarness.SaveScreen(app.LauncherWindow, "a-recording-is-found-by-what-is-said-in-it.png");
    });

    [Fact(DisplayName = "MP3 and FLAC recordings are read too")]
    public Task Mp3AndFlacRecordingsAreReadToo() => AppHarness.RunAsync(fixture, async app =>
    {
        var mp3 = $"{AppHarness.Unique()}.mp3";
        var flac = $"{AppHarness.Unique()}.flac";
        var tone = $"{AppHarness.Unique()}.wav";
        app.SaveFixture("speech.mp3", $"Recordings/{mp3}");
        app.SaveWithFfmpeg($"Recordings/{flac}", "-i fixture:speech.wav");
        app.SaveWithFfmpeg($"Recordings/{tone}", ToneArguments);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(Query,
            () => app.ResultNames.Contains(mp3) && app.ResultNames.Contains(flac));

        var names = app.ResultNames.ToList();
        AssertListedAbove(names, mp3, tone);
        AssertListedAbove(names, flac, tone);

        AppHarness.SaveScreen(app.LauncherWindow, "mp3-and-flac-recordings-are-read-too.png");
    });

    [Fact(DisplayName = "a long recording is heard to its end")]
    public Task ALongRecordingIsHeardToItsEnd() => AppHarness.RunAsync(fixture, async app =>
    {
        var longRecording = $"{AppHarness.Unique()}.wav";
        var tone = $"{AppHarness.Unique()}.wav";
        app.SaveWithFfmpeg($"Recordings/{longRecording}",
            "-f lavfi -t 150 -i anullsrc=r=16000:cl=mono -i fixture:speech.wav "
            + "-filter_complex [1:a]aresample=16000,aformat=channel_layouts=mono[s];[0:a][s]concat=n=2:v=0:a=1 "
            + "-ac 1 -ar 16000");
        app.SaveWithFfmpeg($"Recordings/{tone}", ToneArguments);

        await app.StartAsync();
        await app.WaitUntilIndexedAsync();
        await app.SearchUntilAsync(Query, () => app.ResultNames.Contains(longRecording));

        AssertListedAbove(app.ResultNames.ToList(), longRecording, tone);

        AppHarness.SaveScreen(app.LauncherWindow, "a-long-recording-is-heard-to-its-end.png");
    });
}
