using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// <see cref="IMediaDecoder"/> for the indexer. Pictures: SkiaSharp (PNG, JPEG, GIF, BMP, WebP, ICO), plus the system
/// decoder for HEIC on macOS. WAV audio: read directly. Other audio and video: Media Foundation on Windows,
/// AVFoundation on macOS, and an installed <c>ffmpeg</c> on Linux (or as a fallback anywhere). Files no decoder
/// understands yield null and stay findable by name.
/// </summary>
public sealed partial class MediaDecoder : IMediaDecoder
{
    /// <summary>Default wall-clock budget for decoding one file.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Clips shorter than this are not worth embedding.</summary>
    internal static readonly TimeSpan MinClipLength = TimeSpan.FromSeconds(0.5);

    private readonly IReadOnlyList<IAvBackend> _backends;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    /// <summary>Uses the platform's decoders (probed once).</summary>
    public MediaDecoder(ILogger? logger = null, TimeSpan? timeout = null)
        : this(DefaultBackends(), logger, timeout)
    {
    }

    internal MediaDecoder(IReadOnlyList<IAvBackend> backends, ILogger? logger = null, TimeSpan? timeout = null)
    {
        _backends = backends;
        _logger = logger ?? NullLogger.Instance;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Names of the audio/video decoders in use, in order of preference (diagnostics, tests).</summary>
    public IReadOnlyList<string> BackendNames => _backends.Select(b => b.Name).ToList();

    /// <summary>The platform decoders, best first.</summary>
    internal static IReadOnlyList<IAvBackend> DefaultBackends()
    {
        var list = new List<IAvBackend>();
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(6, 1) && MediaFoundationBackend.TryCreate() is { } mf)
                list.Add(mf);
            if (OperatingSystem.IsMacOSVersionAtLeast(10, 15) && AvFoundationBackend.TryCreate() is { } av)
                list.Add(av);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeLoadException)
        {
        }

        if (FfmpegBackend.TryCreate() is { } ffmpeg)
            list.Add(ffmpeg);
        return list;
    }

    /// <inheritdoc />
    public Task<DecodedImage?> DecodeImageAsync(string path, int maxSide, CancellationToken cancellationToken) =>
        Guard(path, token =>
        {
            if (SkiaImageDecoder.CanDecode(path))
                return SkiaImageDecoder.Decode(path, maxSide, token);
            if (OperatingSystem.IsMacOSVersionAtLeast(10, 15) && AvFoundationBackend.CanDecodeImage(path))
                return AvFoundationBackend.DecodeImage(path, maxSide, token);
            return null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AudioClip>?> DecodeAudioAsync(string path, int maxClips, CancellationToken cancellationToken) =>
        Guard<IReadOnlyList<AudioClip>>(path, token =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxClips, 1);
            if (WavReader.Probe(path) is { } wav)
            {
                return ClipWindows(wav.Duration, maxClips)
                    .Select(w => new AudioClip(AudioResampler.Resample(WavReader.Read(path, wav, w.Start, w.Length, token), wav.SampleRate, AudioClip.SampleRate), w.Start))
                    .Where(c => c.Samples.Length >= MinClipLength.TotalSeconds * AudioClip.SampleRate)
                    .ToList();
            }

            foreach (var backend in _backends)
            {
                token.ThrowIfCancellationRequested();
                var info = backend.Probe(path, token);
                if (info is null)
                    continue;
                if (!info.HasAudio)
                    return [];
                var clips = new List<AudioClip>();
                foreach (var window in ClipWindows(info.Duration, maxClips))
                {
                    var samples = backend.DecodeAudio(path, window.Start, window.Length, token);
                    if (samples is not null && samples.Length >= MinClipLength.TotalSeconds * AudioClip.SampleRate)
                        clips.Add(new AudioClip(Trim(samples), window.Start));
                }

                if (clips.Count > 0)
                    return clips;
            }

            return null;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<VideoFrames?> DecodeVideoAsync(string path, int maxFrames, int maxSide, CancellationToken cancellationToken) =>
        Guard(path, token =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
            foreach (var backend in _backends)
            {
                token.ThrowIfCancellationRequested();
                var info = backend.Probe(path, token);
                if (info is null || !info.HasVideo)
                    continue;
                var times = FrameTimes(info.Duration, maxFrames);
                var (width, height) = FitSize(info.Width, info.Height, maxSide);
                var frames = backend.DecodeFrames(path, times, width, height, token);
                if (frames is null)
                    continue;
                var kept = new List<DecodedImage>();
                var stamps = new List<TimeSpan>();
                for (var i = 0; i < frames.Count && i < times.Count; i++)
                {
                    if (frames[i] is { } frame)
                    {
                        kept.Add(frame);
                        stamps.Add(times[i]);
                    }
                }

                if (kept.Count > 0)
                    return new VideoFrames(kept, stamps, info.Duration);
            }

            return null;
        }, cancellationToken);

    /// <summary>
    /// The audio windows to embed: consecutive <see cref="AudioClip.MaxSeconds"/>-second windows from the start when
    /// they cover the whole file, otherwise <paramref name="maxClips"/> windows spread evenly (first at 0, last ending at
    /// the end) so a long recording is represented from beginning to end.
    /// </summary>
    internal static List<(TimeSpan Start, TimeSpan Length)> ClipWindows(TimeSpan duration, int maxClips)
    {
        var clip = TimeSpan.FromSeconds(AudioClip.MaxSeconds);
        var windows = new List<(TimeSpan, TimeSpan)>();
        if (duration <= TimeSpan.Zero)
            return windows;
        if (duration <= clip * maxClips)
        {
            for (var start = TimeSpan.Zero; start < duration; start += clip)
            {
                var length = duration - start < clip ? duration - start : clip;
                if (length >= MinClipLength || start == TimeSpan.Zero)
                    windows.Add((start, length));
            }

            return windows;
        }

        var span = duration - clip;
        for (var i = 0; i < maxClips; i++)
        {
            var start = maxClips == 1 ? TimeSpan.Zero : span * i / (maxClips - 1);
            windows.Add((TimeSpan.FromSeconds(Math.Floor(start.TotalSeconds)), clip));
        }

        return windows;
    }

    /// <summary>
    /// The reference video sampling: one frame per second (frames at 0 s, 1 s, … for <c>max(1, floor(duration))</c>
    /// seconds), thinned out with <c>linspace(0, n - 1, maxFrames)</c> when there are more than
    /// <paramref name="maxFrames"/>.
    /// </summary>
    internal static List<TimeSpan> FrameTimes(TimeSpan duration, int maxFrames)
    {
        var seconds = Math.Max(1, (int)Math.Floor(duration.TotalSeconds));
        var times = new List<TimeSpan>();
        if (seconds <= maxFrames)
        {
            for (var i = 0; i < seconds; i++)
                times.Add(TimeSpan.FromSeconds(i));
            return times;
        }

        for (var i = 0; i < maxFrames; i++)
            times.Add(TimeSpan.FromSeconds(maxFrames == 1 ? 0 : (int)((double)(seconds - 1) * i / (maxFrames - 1))));
        return times;
    }

    /// <summary>Size for a frame: the video's own, scaled down (aspect kept, even sides) to fit
    /// <paramref name="maxSide"/>.</summary>
    internal static (int Width, int Height) FitSize(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0)
            return (maxSide, maxSide * 9 / 16 / 2 * 2);
        var longest = Math.Max(width, height);
        if (longest <= maxSide)
            return (width, height);
        var scale = (double)maxSide / longest;
        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    /// <summary>Caps a clip at the encoder's 30 s (decoders may return a few samples more).</summary>
    private static float[] Trim(float[] samples)
    {
        const int max = AudioClip.MaxSeconds * AudioClip.SampleRate;
        return samples.Length <= max ? samples : samples[..max];
    }

    private Task<T?> Guard<T>(string path, Func<CancellationToken, T?> body, CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return Task.FromResult(body(deadline.Token));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogTimedOut(_logger, path, _timeout);
            return Task.FromResult<T?>(null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Codecs fail in many ways on damaged files; none of them may stop indexing.
            LogFailed(_logger, e, path);
            return Task.FromResult<T?>(null);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Decoding {Path} took longer than {Timeout}; indexing it by name only")]
    private static partial void LogTimedOut(ILogger logger, string path, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Decoding {Path} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string path);
}
