using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction.Media;

/// <summary>What a backend found in an audio or video file.</summary>
/// <param name="Duration">Length of the file.</param>
/// <param name="HasAudio">True when it has an audio track.</param>
/// <param name="HasVideo">True when it has a video track (cover art in a music file does not count).</param>
/// <param name="Width">Upright width of the video (rotation applied), 0 without video.</param>
/// <param name="Height">Upright height of the video, 0 without video.</param>
internal sealed record AvInfo(TimeSpan Duration, bool HasAudio, bool HasVideo, int Width, int Height);

/// <summary>
/// An audio/video decoder: the operating system's (Media Foundation, AVFoundation) or an installed <c>ffmpeg</c>.
/// Methods return null when the file cannot be decoded; only cancellation throws.
/// </summary>
internal interface IAvBackend
{
    string Name { get; }

    AvInfo? Probe(string path, CancellationToken cancellationToken);

    /// <summary>Mono 16 kHz samples of [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>).</summary>
    float[]? DecodeAudio(string path, TimeSpan start, TimeSpan length, CancellationToken cancellationToken);

    /// <summary>The upright frame shown at each of <paramref name="times"/>, scaled to <paramref name="width"/> ×
    /// <paramref name="height"/>. An entry is null when that frame could not be decoded.</summary>
    IReadOnlyList<DecodedImage?>? DecodeFrames(string path, IReadOnlyList<TimeSpan> times, int width, int height, CancellationToken cancellationToken);
}
