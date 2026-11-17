using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Decodes audio and video with the <c>ffmpeg</c> and <c>ffprobe</c> commands when they are installed (most Linux
/// desktops have them; on Windows and macOS they are only a fallback behind the system decoders). Valafind does not ship
/// FFmpeg. Each call is a short-lived single-threaded process that inherits the indexing thread's low priority.
/// </summary>
internal sealed class FfmpegBackend : IAvBackend
{
    private const int MaxProbeOutput = 1 << 20;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    private FfmpegBackend(string ffmpeg, string ffprobe)
    {
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
    }

    public string Name => "ffmpeg";

    /// <summary>The backend, or null when <c>ffmpeg</c> or <c>ffprobe</c> is not installed.</summary>
    public static FfmpegBackend? TryCreate()
    {
        var ffmpeg = ExecutableFinder.Find("ffmpeg");
        var ffprobe = ExecutableFinder.Find("ffprobe");
        return ffmpeg is null || ffprobe is null ? null : new FfmpegBackend(ffmpeg, ffprobe);
    }

    public AvInfo? Probe(string path, CancellationToken cancellationToken)
    {
        var output = ProcessRunner.Run(_ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", Path.GetFullPath(path)],
            MaxProbeOutput, ProbeTimeout, cancellationToken);
        return output is null ? null : ParseProbe(output);
    }

    internal static AvInfo? ParseProbe(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var duration = 0.0;
            if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

            bool hasAudio = false, hasVideo = false;
            int width = 0, height = 0;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "audio")
                    {
                        hasAudio = true;
                        if (duration <= 0 && stream.TryGetProperty("duration", out var ad))
                            double.TryParse(ad.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                    }
                    else if (type == "video" && !hasVideo)
                    {
                        // Cover art in music files is a one-frame "video" stream marked attached_pic.
                        if (stream.TryGetProperty("disposition", out var disposition) &&
                            disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1)
                            continue;
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        if (width <= 0 || height <= 0)
                            continue;
                        hasVideo = true;
                        if (Math.Abs(Rotation(stream)) % 180 == 90)
                            (width, height) = (height, width);
                    }
                }
            }

            return duration > 0 && (hasAudio || hasVideo)
                ? new AvInfo(TimeSpan.FromSeconds(duration), hasAudio, hasVideo, width, height)
                : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(r.GetDouble());
            }
        }

        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate) &&
            int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
            return degrees;
        return 0;
    }

    public float[]? DecodeAudio(string path, TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        var maxBytes = (int)Math.Ceiling(length.TotalSeconds * AudioClip.SampleRate) * sizeof(float) + 4096;
        var output = ProcessRunner.Run(_ffmpeg,
        [
            "-nostdin", "-v", "error", "-threads", "1",
            "-ss", Seconds(start), "-t", Seconds(length), "-i", Path.GetFullPath(path),
            "-vn", "-ac", "1", "-ar", AudioClip.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "pipe:1",
        ], maxBytes, Timeout.InfiniteTimeSpan, cancellationToken);
        if (output is null)
            return null;
        var samples = new float[output.Length / sizeof(float)];
        Buffer.BlockCopy(output, 0, samples, 0, samples.Length * sizeof(float));
        return samples;
    }

    public IReadOnlyList<DecodedImage?>? DecodeFrames(string path, IReadOnlyList<TimeSpan> times, int width, int height, CancellationToken cancellationToken)
    {
        var frames = new List<DecodedImage?>(times.Count);
        var bytes = width * height * 3;
        foreach (var time in times)
        {
            var output = ProcessRunner.Run(_ffmpeg,
            [
                "-nostdin", "-v", "error", "-threads", "1",
                "-ss", Seconds(time), "-i", Path.GetFullPath(path),
                "-an", "-frames:v", "1",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=bicubic"),
                "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
            ], bytes, Timeout.InfiniteTimeSpan, cancellationToken);
            frames.Add(output is not null && output.Length == bytes ? new DecodedImage(width, height, output) : null);
        }

        return frames;
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Finds a command on PATH (and the usual install folders a desktop session's PATH may lack).</summary>
internal static class ExecutableFinder
{
    public static string? Find(string name)
    {
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!OperatingSystem.IsWindows())
            directories.AddRange(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin", "/opt/local/bin", "/snap/bin"]);
        foreach (var directory in directories)
        {
            try
            {
                if (!Path.IsPathFullyQualified(directory))
                    continue; // never run a program from the current directory
                var candidate = Path.Combine(directory, file);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

/// <summary>Runs a command and collects its standard output (capped), killing it on cancellation or timeout.</summary>
internal static class ProcessRunner
{
    /// <summary>Standard output, or null when the command failed (non-zero exit, could not start, timed out).</summary>
    public static byte[]? Run(string executable, IReadOnlyList<string> arguments, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = ReadCappedAsync(process.StandardOutput.BaseStream, maxBytes, deadline.Token);
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return null; // timed out
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var keep = (int)Math.Min(read, Math.Max(0, maxBytes - memory.Length));
            memory.Write(buffer, 0, keep); // the rest is drained so the process can finish
        }

        return memory.ToArray();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}
