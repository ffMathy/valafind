using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.Core.Abstractions;
using Valafind.Core.Embedding.Media;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Audio and video decoding with Windows Media Foundation's Source Reader (MP3, AAC/M4A, WMA, FLAC, MP4/MOV/H.264,
/// HEVC and VP9 where their codecs are installed, AVI, WMV, …). Called through raw COM vtables so the app keeps a plain
/// <c>net10.0</c> target. Audio is read as 32-bit float at the file's own rate and resampled to 16 kHz here; video frames
/// as RGB32 (the reader's video processor converts from YUV), then rotated upright and scaled.
/// </summary>
/// <remarks>Interface IDs, vtable slots and constants are from the Win32 metadata (Windows.Win32.winmd); slots 0–2 are
/// IUnknown, and IMFAttributes methods occupy slots 3–32 of every interface derived from it.</remarks>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe partial class MediaFoundationBackend : IAvBackend
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint EndOfStream = 0x2;
    private const uint CurrentMediaTypeChanged = 0x20;
    private const ushort VtI8 = 20;
    private const int MaxFramesPerSeek = 900;

    private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    private static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    // vtable slots
    private const int AttrGetUInt32 = 7, AttrGetUInt64 = 8, AttrSetUInt32 = 21, AttrSetGuid = 24;
    private const int ReaderSetStreamSelection = 4, ReaderGetNativeMediaType = 5, ReaderGetCurrentMediaType = 6,
        ReaderSetCurrentMediaType = 7, ReaderSetCurrentPosition = 8, ReaderReadSample = 9, ReaderGetPresentationAttribute = 12;
    private const int SampleConvertToContiguousBuffer = 41;
    private const int BufferLock = 3, BufferUnlock = 4;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    private MediaFoundationBackend()
    {
    }

    public string Name => "Media Foundation";

    /// <summary>The backend, or null when Media Foundation is not available (Windows N without the Media Feature Pack,
    /// Server without Desktop Experience).</summary>
    public static MediaFoundationBackend? TryCreate()
    {
        try
        {
            using var session = new Session();
            return session.Started ? new MediaFoundationBackend() : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return null;
        }
    }

    public AvInfo? Probe(string path, CancellationToken cancellationToken)
    {
        using var session = new Session();
        if (!session.Started)
            return null;
        var reader = CreateReader(path, videoProcessing: false);
        if (reader == 0)
            return null;
        try
        {
            PropVariant duration;
            var g = MF_PD_DURATION;
            if (Call(reader, ReaderGetPresentationAttribute, MediaSource, &g, &duration) < 0)
                return null;
            var length = TimeSpan.FromTicks(duration.Value);

            var hasAudio = TryNativeType(reader, FirstAudioStream, out var audioType);
            Release(audioType);
            var hasVideo = TryNativeType(reader, FirstVideoStream, out var videoType);
            int width = 0, height = 0;
            if (hasVideo)
            {
                try
                {
                    var size = GetUInt64(videoType, MF_MT_FRAME_SIZE) ?? 0;
                    width = (int)(size >> 32);
                    height = (int)(size & 0xFFFFFFFF);
                    if (Rotation(videoType) % 180 == 90)
                        (width, height) = (height, width);
                    hasVideo = width > 0 && height > 0;
                }
                finally
                {
                    Release(videoType);
                }
            }

            return length > TimeSpan.Zero && (hasAudio || hasVideo) ? new AvInfo(length, hasAudio, hasVideo, width, height) : null;
        }
        finally
        {
            Release(reader);
        }
    }

    public float[]? DecodeAudio(string path, TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        using var session = new Session();
        if (!session.Started)
            return null;
        var reader = CreateReader(path, videoProcessing: false);
        if (reader == 0)
            return null;
        nint type = 0, current = 0;
        try
        {
            if (Call(reader, ReaderSetStreamSelection, AllStreams, 0) < 0 || Call(reader, ReaderSetStreamSelection, FirstAudioStream, 1) < 0)
                return null;
            if (MFCreateMediaType(&type) < 0)
                return null;
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(type, MF_MT_SUBTYPE, MFAudioFormat_Float);
            if (Call(reader, ReaderSetCurrentMediaType, FirstAudioStream, (nint)0, type) < 0)
                return null;
            if (Call(reader, ReaderGetCurrentMediaType, FirstAudioStream, &current) < 0)
                return null;
            var (channels, rate) = AudioFormat(current);
            if (channels <= 0 || rate <= 0)
                return null;

            Seek(reader, start);
            var end = start + length;
            var mono = new List<float>((int)(length.TotalSeconds * rate) + rate);
            long firstFrameTicks = -1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (sample, flags, timestamp) = ReadSample(reader, FirstAudioStream);
                if (sample == 0)
                {
                    if ((flags & EndOfStream) != 0)
                        break;
                    continue;
                }

                try
                {
                    if ((flags & CurrentMediaTypeChanged) != 0)
                    {
                        // Decoders (AAC especially) settle their real output format only once they have seen the
                        // stream: what is delivered from here on follows the new type.
                        Release(current);
                        current = 0;
                        if (Call(reader, ReaderGetCurrentMediaType, FirstAudioStream, &current) < 0)
                            return null;
                        var (newChannels, newRate) = AudioFormat(current);
                        if (newChannels <= 0 || newRate <= 0)
                            return null;
                        if (newRate != rate && mono.Count > 0)
                        {
                            var converted = AudioResampler.Resample(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mono), rate, newRate);
                            mono.Clear();
                            mono.AddRange(converted);
                        }

                        (channels, rate) = (newChannels, newRate);
                    }

                    if (firstFrameTicks < 0)
                        firstFrameTicks = timestamp;
                    CopyAudio(sample, channels, mono);
                }
                finally
                {
                    Release(sample);
                }

                // Stop on the decoder's own time stamps (not on a sample count, which depends on the reported format).
                if (timestamp >= end.Ticks || TimeSpan.FromTicks(firstFrameTicks) + TimeSpan.FromSeconds((double)mono.Count / rate) >= end + TimeSpan.FromSeconds(5))
                    break;
            }

            if (mono.Count == 0)
                return null;
            // Drop what the decoder delivered before the requested start, and anything past the end.
            var skip = firstFrameTicks < start.Ticks ? (int)((start.Ticks - firstFrameTicks) * rate / TimeSpan.TicksPerSecond) : 0;
            var take = Math.Min(mono.Count - Math.Min(skip, mono.Count), (int)Math.Ceiling(length.TotalSeconds * rate));
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mono).Slice(Math.Min(skip, mono.Count), Math.Max(0, take));
            return AudioResampler.Resample(span, rate, AudioClip.SampleRate);
        }
        finally
        {
            Release(current);
            Release(type);
            Release(reader);
        }
    }

    public IReadOnlyList<DecodedImage?>? DecodeFrames(string path, IReadOnlyList<TimeSpan> times, int width, int height, CancellationToken cancellationToken)
    {
        using var session = new Session();
        if (!session.Started)
            return null;
        var reader = CreateReader(path, videoProcessing: true);
        if (reader == 0)
            return null;
        nint type = 0, current = 0, native = 0;
        try
        {
            if (Call(reader, ReaderSetStreamSelection, AllStreams, 0) < 0 || Call(reader, ReaderSetStreamSelection, FirstVideoStream, 1) < 0)
                return null;
            var rotation = TryNativeType(reader, FirstVideoStream, out native) ? Rotation(native) : 0;
            if (MFCreateMediaType(&type) < 0)
                return null;
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(type, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
            if (Call(reader, ReaderSetCurrentMediaType, FirstVideoStream, (nint)0, type) < 0)
                return null;
            if (Call(reader, ReaderGetCurrentMediaType, FirstVideoStream, &current) < 0)
                return null;
            var size = GetUInt64(current, MF_MT_FRAME_SIZE) ?? 0;
            var frameWidth = (int)(size >> 32);
            var frameHeight = (int)(size & 0xFFFFFFFF);
            if (frameWidth <= 0 || frameHeight <= 0)
                return null;
            var stride = (int?)GetUInt32(current, MF_MT_DEFAULT_STRIDE) ?? frameWidth * 4;

            var frames = new List<DecodedImage?>(times.Count);
            foreach (var time in times)
            {
                cancellationToken.ThrowIfCancellationRequested();
                frames.Add(ReadFrameAt(reader, time, ref frameWidth, ref frameHeight, ref stride, rotation, width, height, cancellationToken));
            }

            return frames;
        }
        finally
        {
            Release(native);
            Release(current);
            Release(type);
            Release(reader);
        }
    }

    private static (int Channels, int Rate) AudioFormat(nint mediaType) =>
        ((int)(GetUInt32(mediaType, MF_MT_AUDIO_NUM_CHANNELS) ?? 0), (int)(GetUInt32(mediaType, MF_MT_AUDIO_SAMPLES_PER_SECOND) ?? 0));

    private static DecodedImage? ReadFrameAt(nint reader, TimeSpan time, ref int frameWidth, ref int frameHeight, ref int stride, int rotation,
        int width, int height, CancellationToken cancellationToken)
    {
        Seek(reader, time);
        // Seeking lands on the key frame before `time`; decode forward to the first frame at or after it.
        var tolerance = TimeSpan.FromMilliseconds(20).Ticks;
        for (var i = 0; i < MaxFramesPerSeek; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sample, flags, timestamp) = ReadSample(reader, FirstVideoStream);
            if (sample == 0)
            {
                if ((flags & EndOfStream) != 0)
                    return null;
                continue;
            }

            try
            {
                if ((flags & CurrentMediaTypeChanged) != 0)
                {
                    nint changed = 0;
                    if (Call(reader, ReaderGetCurrentMediaType, FirstVideoStream, &changed) >= 0 && changed != 0)
                    {
                        var size = GetUInt64(changed, MF_MT_FRAME_SIZE) ?? 0;
                        if (size != 0)
                        {
                            frameWidth = (int)(size >> 32);
                            frameHeight = (int)(size & 0xFFFFFFFF);
                            stride = (int?)GetUInt32(changed, MF_MT_DEFAULT_STRIDE) ?? frameWidth * 4;
                        }

                        Release(changed);
                    }
                }

                if (timestamp + tolerance < time.Ticks && i < MaxFramesPerSeek - 1)
                    continue;
                var rgb = CopyFrame(sample, frameWidth, frameHeight, stride);
                if (rgb is null)
                    return null;
                var image = RotateCounterClockwise(new DecodedImage(frameWidth, frameHeight, rgb), rotation);
                return image.Width == width && image.Height == height
                    ? image
                    : new DecodedImage(width, height, BicubicResampler.Resize(image.Rgb, image.Width, image.Height, width, height));
            }
            finally
            {
                Release(sample);
            }
        }

        return null;
    }

    private static byte[]? CopyFrame(nint sample, int width, int height, int stride)
    {
        nint buffer = 0;
        if (Call(sample, SampleConvertToContiguousBuffer, &buffer) < 0 || buffer == 0)
            return null;
        try
        {
            byte* data;
            uint max, length;
            if (Call(buffer, BufferLock, &data, &max, &length) < 0)
                return null;
            try
            {
                var absStride = Math.Abs(stride);
                if (length < (long)absStride * height || absStride < width * 4)
                    return null;
                var rgb = new byte[width * height * 3];
                for (var y = 0; y < height; y++)
                {
                    // A negative stride means the rows are stored bottom-up.
                    var row = data + (stride >= 0 ? y : height - 1 - y) * absStride;
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 3;
                        rgb[o] = row[x * 4 + 2];
                        rgb[o + 1] = row[x * 4 + 1];
                        rgb[o + 2] = row[x * 4];
                    }
                }

                return rgb;
            }
            finally
            {
                Call(buffer, BufferUnlock);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    private static void CopyAudio(nint sample, int channels, List<float> mono)
    {
        nint buffer = 0;
        if (Call(sample, SampleConvertToContiguousBuffer, &buffer) < 0 || buffer == 0)
            return;
        try
        {
            byte* data;
            uint max, length;
            if (Call(buffer, BufferLock, &data, &max, &length) < 0)
                return;
            try
            {
                var floats = (float*)data;
                var frames = (int)(length / sizeof(float) / channels);
                for (var f = 0; f < frames; f++)
                {
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                        sum += floats[f * channels + c];
                    mono.Add(sum / channels);
                }
            }
            finally
            {
                Call(buffer, BufferUnlock);
            }
        }
        finally
        {
            Release(buffer);
        }
    }

    /// <summary>MF_MT_VIDEO_ROTATION: degrees the frame must be turned counter-clockwise to be upright.</summary>
    private static int Rotation(nint mediaType) => (int)((GetUInt32(mediaType, MF_MT_VIDEO_ROTATION) ?? 0) % 360);

    private static DecodedImage RotateCounterClockwise(DecodedImage image, int degrees)
    {
        int w = image.Width, h = image.Height;
        if (degrees % 360 == 0)
            return image;
        var swap = degrees % 180 == 90;
        var outWidth = swap ? h : w;
        var outHeight = swap ? w : h;
        var output = new byte[image.Rgb.Length];
        for (var y = 0; y < outHeight; y++)
        {
            for (var x = 0; x < outWidth; x++)
            {
                var (sx, sy) = degrees switch
                {
                    90 => (w - 1 - y, x),
                    180 => (w - 1 - x, h - 1 - y),
                    _ => (y, h - 1 - x), // 270 counter-clockwise = 90 clockwise
                };
                var s = (sy * w + sx) * 3;
                var o = (y * outWidth + x) * 3;
                output[o] = image.Rgb[s];
                output[o + 1] = image.Rgb[s + 1];
                output[o + 2] = image.Rgb[s + 2];
            }
        }

        return new DecodedImage(outWidth, outHeight, output);
    }

    private static nint CreateReader(string path, bool videoProcessing)
    {
        nint attributes = 0, reader = 0;
        try
        {
            if (MFCreateAttributes(&attributes, 1) < 0)
                return 0;
            if (videoProcessing)
            {
                var key = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                Call(attributes, AttrSetUInt32, &key, 1u);
            }

            fixed (char* p = Path.GetFullPath(path))
            {
                if (MFCreateSourceReaderFromURL(p, attributes, &reader) < 0)
                    return 0;
            }

            return reader;
        }
        finally
        {
            Release(attributes);
        }
    }

    private static bool TryNativeType(nint reader, uint stream, out nint type)
    {
        nint t = 0;
        var ok = Call(reader, ReaderGetNativeMediaType, stream, 0u, &t) >= 0 && t != 0;
        type = ok ? t : 0;
        return ok;
    }

    private static void Seek(nint reader, TimeSpan position)
    {
        var format = Guid.Empty;
        var value = new PropVariant { Type = VtI8, Value = Math.Max(0, position.Ticks) };
        Call(reader, ReaderSetCurrentPosition, &format, &value);
    }

    private static (nint Sample, uint Flags, long Timestamp) ReadSample(nint reader, uint stream)
    {
        uint actual, flags;
        long timestamp;
        nint sample = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, ReaderReadSample))(
            reader, stream, 0, &actual, &flags, &timestamp, &sample);
        if (hr < 0)
            return (0, EndOfStream, 0);
        return (sample, flags, timestamp);
    }

    private static uint? GetUInt32(nint attributes, Guid key)
    {
        uint value;
        return Call(attributes, AttrGetUInt32, &key, &value) >= 0 ? value : null;
    }

    private static ulong? GetUInt64(nint attributes, Guid key)
    {
        ulong value;
        return Call(attributes, AttrGetUInt64, &key, &value) >= 0 ? value : null;
    }

    private static void SetGuid(nint attributes, Guid key, Guid value) => Call(attributes, AttrSetGuid, &key, &value);

    private static void* Slot(nint unknown, int index) => (*(void***)unknown)[index];

    private static int Call(nint p, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(p, slot))(p);

    private static int Call(nint p, int slot, uint a, int b) => ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, uint a, nint b, nint c) => ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(p, slot))(p, a, b, c);

    private static int Call(nint p, int slot, uint a, nint* b) => ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, uint a, uint b, nint* c) => ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(p, slot))(p, a, b, c);

    private static int Call(nint p, int slot, uint a, Guid* b, PropVariant* c) => ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, PropVariant*, int>)Slot(p, slot))(p, a, b, c);

    private static int Call(nint p, int slot, Guid* a, PropVariant* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, Guid* a, uint b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, Guid* a, uint* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, Guid* a, ulong* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, Guid* a, Guid* b) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(p, slot))(p, a, b);

    private static int Call(nint p, int slot, nint* a) => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(p, slot))(p, a);

    private static int Call(nint p, int slot, byte** a, uint* b, uint* c) => ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(p, slot))(p, a, b, c);

    private static void Release(nint unknown)
    {
        if (unknown != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(unknown, 2))(unknown);
    }

    /// <summary>COM (multithreaded apartment) and Media Foundation for the duration of one call.</summary>
    private readonly struct Session : IDisposable
    {
        private readonly bool _com;

        public Session()
        {
            var hr = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
            _com = hr is 0 or 1;
            Started = MFStartup(MfVersion, MfStartupLite) >= 0;
        }

        public bool Started { get; }

        public void Dispose()
        {
            if (Started)
                _ = MFShutdown();
            if (_com)
                CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(nint* attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(nint* mediaType);

    [LibraryImport("mfreadwrite.dll")]
    private static partial int MFCreateSourceReaderFromURL(char* url, nint attributes, nint* reader);
}
