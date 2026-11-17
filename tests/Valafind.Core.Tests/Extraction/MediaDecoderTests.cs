using Valafind.Core.Abstractions;
using Valafind.Core.Extraction.Media;
using Xunit;

namespace Valafind.Core.Tests.Extraction;

/// <summary>Media decoding with the platform's decoders (Media Foundation, AVFoundation or ffmpeg). Tests that need a
/// decoder skip when the machine has none.</summary>
public sealed class MediaDecoderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "valafind-media-" + Guid.NewGuid().ToString("N"));

    public MediaDecoderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MediaDecoder RequireBackend()
    {
        var decoder = new MediaDecoder();
        Assert.SkipWhen(decoder.BackendNames.Count == 0, "No audio/video decoder on this machine (install ffmpeg on Linux).");
        return decoder;
    }

    [Fact]
    public async Task Wav_is_read_without_a_codec()
    {
        var clips = await new MediaDecoder([]).DecodeAudioAsync(Fixture("speech.wav"), 4, Ct);
        var clip = Assert.Single(clips!);
        Assert.Equal(TimeSpan.Zero, clip.Offset);
        Assert.InRange(clip.Samples.Length, 5 * 16_000, 8 * 16_000);
        Assert.Contains(clip.Samples, s => Math.Abs(s) > 0.1f);
    }

    [Fact]
    public async Task Stereo_44k_wav_is_downmixed_and_resampled()
    {
        // 1 kHz in the left channel, silence in the right, 2 s at 44.1 kHz, 16-bit.
        var path = Path.Combine(_root, "tone.wav");
        WriteWav(path, 44_100, 2, 2.0, (t, channel) => channel == 0 ? 0.8 * Math.Sin(2 * Math.PI * 1000 * t) : 0);
        var clip = Assert.Single((await new MediaDecoder([]).DecodeAudioAsync(path, 4, Ct))!);
        Assert.InRange(clip.Samples.Length, 31_900, 32_000);
        // Mono = average of the channels: amplitude 0.4; resampling keeps the 1 kHz tone.
        var peak = clip.Samples.Skip(1000).Take(30_000).Max(Math.Abs);
        Assert.InRange(peak, 0.38f, 0.42f);
        var crossings = 0;
        for (var i = 1001; i < 17_000; i++)
        {
            if (clip.Samples[i - 1] < 0 && clip.Samples[i] >= 0)
                crossings++;
        }

        Assert.InRange(crossings, 995, 1005); // 1 s at 1 kHz
    }

    [Fact]
    public async Task Not_a_wav_and_missing_files_yield_null()
    {
        var bogus = Path.Combine(_root, "bogus.mp3");
        await File.WriteAllBytesAsync(bogus, new byte[1000], Ct);
        var decoder = new MediaDecoder();
        Assert.Null(await decoder.DecodeAudioAsync(bogus, 4, Ct));
        Assert.Null(await decoder.DecodeVideoAsync(bogus, 8, 1280, Ct));
        Assert.Null(await decoder.DecodeImageAsync(Path.Combine(_root, "missing.png"), 1024, Ct));
        Assert.Null(await decoder.DecodeImageAsync(bogus.Replace(".mp3", ".png", StringComparison.Ordinal), 1024, Ct));
    }

    [Fact]
    public async Task Mp3_is_decoded_by_the_platform()
    {
        var decoder = RequireBackend();
        var clip = Assert.Single((await decoder.DecodeAudioAsync(Fixture("speech.mp3"), 4, Ct))!);
        Assert.InRange(clip.Samples.Length, 5 * 16_000, 8 * 16_000);
        Assert.Contains(clip.Samples, s => Math.Abs(s) > 0.1f);
    }

    [Fact]
    public async Task Video_frames_and_its_audio_track_are_decoded()
    {
        var decoder = RequireBackend();
        var video = await decoder.DecodeVideoAsync(Fixture("clip.mp4"), 8, 1280, Ct);
        Assert.NotNull(video);
        Assert.Equal(4, video.Frames.Count); // 4 s at one frame per second
        Assert.Equal([0, 1, 2, 3], video.Timestamps.Select(t => (int)t.TotalSeconds));
        Assert.All(video.Frames, f => Assert.Equal((192, 108), (f.Width, f.Height)));
        Assert.InRange(video.Duration.TotalSeconds, 3.9, 4.2);
        // testsrc2 frames differ from second to second.
        Assert.NotEqual(video.Frames[0].Rgb, video.Frames[3].Rgb);

        var audio = Assert.Single((await decoder.DecodeAudioAsync(Fixture("clip.mp4"), 2, Ct))!);
        Assert.InRange(audio.Samples.Length, 3 * 16_000, 5 * 16_000);

        var small = await decoder.DecodeVideoAsync(Fixture("clip.mp4"), 2, 96, Ct);
        Assert.Equal(2, small!.Frames.Count);
        Assert.Equal([0, 3], small.Timestamps.Select(t => (int)t.TotalSeconds));
        Assert.All(small.Frames, f => Assert.Equal((96, 54), (f.Width, f.Height)));
    }

    [Fact]
    public void Audio_windows_cover_short_files_and_spread_over_long_ones()
    {
        Assert.Equal([(TimeSpan.Zero, TimeSpan.FromSeconds(10))], MediaDecoder.ClipWindows(TimeSpan.FromSeconds(10), 4));
        Assert.Equal(
            [(TimeSpan.Zero, TimeSpan.FromSeconds(30)), (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)), (TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10))],
            MediaDecoder.ClipWindows(TimeSpan.FromSeconds(70), 4));
        var hour = MediaDecoder.ClipWindows(TimeSpan.FromHours(1), 4);
        Assert.Equal([0, 1190, 2380, 3570], hour.Select(w => (int)w.Start.TotalSeconds));
        Assert.All(hour, w => Assert.Equal(TimeSpan.FromSeconds(30), w.Length));
        Assert.Empty(MediaDecoder.ClipWindows(TimeSpan.Zero, 4));
    }

    [Fact]
    public void Frame_times_follow_one_per_second_sampling()
    {
        Assert.Equal([0], MediaDecoder.FrameTimes(TimeSpan.FromSeconds(0.4), 8).Select(t => (int)t.TotalSeconds));
        Assert.Equal([0, 1, 2], MediaDecoder.FrameTimes(TimeSpan.FromSeconds(3.9), 8).Select(t => (int)t.TotalSeconds));
        Assert.Equal([0, 85, 171, 256, 342, 427, 513, 599], MediaDecoder.FrameTimes(TimeSpan.FromMinutes(10), 8).Select(t => (int)t.TotalSeconds));
    }

    [Fact]
    public void Ffprobe_output_is_parsed_with_rotation_and_cover_art()
    {
        var json = """
            {"streams":[
              {"codec_type":"video","width":1920,"height":1080,"side_data_list":[{"rotation":-90}]},
              {"codec_type":"audio"}],
             "format":{"duration":"12.5"}}
            """u8.ToArray();
        Assert.Equal(new AvInfo(TimeSpan.FromSeconds(12.5), true, true, 1080, 1920), FfmpegBackend.ParseProbe(json));

        var music = """
            {"streams":[{"codec_type":"audio"},{"codec_type":"video","width":600,"height":600,"disposition":{"attached_pic":1}}],
             "format":{"duration":"200.0"}}
            """u8.ToArray();
        Assert.Equal(new AvInfo(TimeSpan.FromSeconds(200), true, false, 0, 0), FfmpegBackend.ParseProbe(music));
        Assert.Null(FfmpegBackend.ParseProbe("not json"u8.ToArray()));
    }

    private static void WriteWav(string path, int rate, int channels, double seconds, Func<double, int, double> signal)
    {
        var frames = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = frames * channels * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
                writer.Write((short)Math.Round(signal((double)i / rate, c) * 32767));
        }
    }
}
