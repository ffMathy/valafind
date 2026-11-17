using System.Buffers.Binary;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Reads RIFF/RF64-less WAVE files (PCM 8/16/24/32-bit, IEEE float 32/64, WAVE_FORMAT_EXTENSIBLE) without any codec:
/// the one audio format every platform can then embed the same way. Channels are averaged to mono.
/// </summary>
internal static class WavReader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    internal sealed record Info(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, long DataOffset, long DataLength)
    {
        public int BlockAlign => Channels * (BitsPerSample / 8);

        public long Frames => DataLength / BlockAlign;

        public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);
    }

    public static Info? Probe(string path)
    {
        using var stream = ExtractionIO.OpenRead(path);
        return Probe(stream);
    }

    internal static Info? Probe(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12)
            return null;
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            return null;

        ushort format = 0, channels = 0, bits = 0;
        uint rate = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
        {
            var id = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                    return null;
                var n = (int)Math.Min(size, (uint)fmt.Length);
                if (stream.ReadAtLeast(fmt[..n], n, throwOnEndOfStream: false) < n)
                    return null;
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == FormatExtensible && n >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // first two bytes of the sub-format GUID
                stream.Seek(size - n + (size & 1), SeekOrigin.Current);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0 || rate == 0 || rate > 1_000_000)
                    return null;
                var isFloat = format == FormatFloat;
                if (!(format == FormatPcm && bits is 8 or 16 or 24 or 32) && !(isFloat && bits is 32 or 64))
                    return null;
                var available = stream.Length - stream.Position;
                var length = size == uint.MaxValue || size > available ? available : size; // streamed WAVs leave size unset
                var info = new Info((int)rate, channels, bits, isFloat, stream.Position, length);
                return info.BlockAlign == 0 ? null : info;
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        return null;
    }

    /// <summary>Mono samples of [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>) at the
    /// file's own sample rate.</summary>
    public static float[] Read(string path, Info info, TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        var firstFrame = Math.Clamp((long)(start.TotalSeconds * info.SampleRate), 0, info.Frames);
        var frames = Math.Clamp((long)(length.TotalSeconds * info.SampleRate), 0, info.Frames - firstFrame);
        var output = new float[frames];
        using var stream = ExtractionIO.OpenRead(path);
        stream.Position = info.DataOffset + firstFrame * info.BlockAlign;
        var bytesPerSample = info.BitsPerSample / 8;
        var buffer = new byte[info.BlockAlign * 4096];
        long done = 0;
        while (done < frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(4096, frames - done) * info.BlockAlign;
            var read = stream.ReadAtLeast(buffer.AsSpan(0, want), want, throwOnEndOfStream: false);
            var got = read / info.BlockAlign;
            if (got == 0)
                break;
            for (var f = 0; f < got; f++)
            {
                double sum = 0;
                for (var c = 0; c < info.Channels; c++)
                    sum += Sample(buffer.AsSpan((f * info.Channels + c) * bytesPerSample, bytesPerSample), info);
                output[done + f] = (float)(sum / info.Channels);
            }

            done += got;
        }

        return done == frames ? output : output[..(int)done];
    }

    private static double Sample(ReadOnlySpan<byte> b, Info info) => info.BitsPerSample switch
    {
        8 => (b[0] - 128) / 128.0,
        16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768.0,
        24 => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608.0,
        32 when info.IsFloat => BinaryPrimitives.ReadSingleLittleEndian(b),
        32 => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0,
        64 => BinaryPrimitives.ReadDoubleLittleEndian(b),
        _ => 0,
    };
}
