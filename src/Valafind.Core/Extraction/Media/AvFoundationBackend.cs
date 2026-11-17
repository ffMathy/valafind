using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Valafind.Core.Abstractions;
using Valafind.Core.Embedding.Media;

namespace Valafind.Core.Extraction.Media;

/// <summary>
/// Audio, video and HEIC decoding with the macOS frameworks: AVFoundation (<c>AVAssetReader</c> resampling audio to
/// 16 kHz mono float, <c>AVAssetImageGenerator</c> for upright video frames) and ImageIO for pictures Skia cannot read
/// (HEIC/HEIF, RAW previews). Called through the Objective-C runtime (<c>objc_msgSend</c>) and the C APIs of CoreMedia,
/// CoreGraphics and ImageIO, so no bindings package is needed.
/// </summary>
[SupportedOSPlatform("macos10.15")]
internal sealed unsafe partial class AvFoundationBackend : IAvBackend
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string AVFoundationPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    private const uint AudioFormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const int AssetReaderStatusReading = 1;
    private const uint ImageAlphaNoneSkipLast = 5; // kCGImageAlphaNoneSkipLast: RGBX
    private const int CmTimeScale = 600;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".tif", ".tiff", ".raw", ".avif",
    };

    private static nint s_avFoundation;
    private static nint s_imageIO;
    private static nint s_msgSend;
    private static nint s_msgSendStret;

    [StructLayout(LayoutKind.Sequential)]
    private struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;

        public static CMTime From(TimeSpan time) => new()
        {
            Value = (long)Math.Round(time.TotalSeconds * CmTimeScale),
            Timescale = CmTimeScale,
            Flags = 1, // kCMTimeFlags_Valid
        };

        public readonly double Seconds => Timescale > 0 && (Flags & 1) != 0 ? (double)Value / Timescale : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMTimeRange
    {
        public CMTime Start;
        public CMTime Duration;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGAffineTransform
    {
        public double A, B, C, D, Tx, Ty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    private AvFoundationBackend()
    {
    }

    public string Name => "AVFoundation";

    public static AvFoundationBackend? TryCreate() => EnsureLoaded() ? new AvFoundationBackend() : null;

    private static bool EnsureLoaded()
    {
        if (s_msgSend != 0)
            return true;
        try
        {
            var objc = NativeLibrary.Load(Objc);
            NativeLibrary.Load(CoreFoundation);
            NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
            NativeLibrary.Load(CoreMedia);
            NativeLibrary.Load(CoreGraphics);
            s_imageIO = NativeLibrary.Load(ImageIO);
            s_avFoundation = NativeLibrary.Load(AVFoundationPath);
            // Structs larger than 16 bytes come back through a hidden pointer, which x64 routes via objc_msgSend_stret.
            s_msgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? NativeLibrary.GetExport(objc, "objc_msgSend_stret")
                : NativeLibrary.GetExport(objc, "objc_msgSend");
            s_msgSend = NativeLibrary.GetExport(objc, "objc_msgSend");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ IAvBackend

    public AvInfo? Probe(string path, CancellationToken cancellationToken)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var asset = CreateAsset(path);
            if (asset == 0)
                return null;
            var duration = MsgCMTime(asset, "duration").Seconds;
            var audioTracks = Tracks(asset, "AVMediaTypeAudio");
            var videoTracks = Tracks(asset, "AVMediaTypeVideo");
            var hasAudio = Count(audioTracks) > 0;
            var hasVideo = Count(videoTracks) > 0;
            int width = 0, height = 0;
            if (hasVideo)
            {
                var track = Msg(videoTracks, "objectAtIndex:", (nint)0);
                var size = MsgSize(track, "naturalSize");
                var transform = MsgTransform(track, "preferredTransform");
                width = (int)Math.Round(Math.Abs(size.Width));
                height = (int)Math.Round(Math.Abs(size.Height));
                if (Math.Abs(transform.B) > 0.5 && Math.Abs(transform.C) > 0.5)
                    (width, height) = (height, width); // rotated by 90° or 270°
                hasVideo = width > 0 && height > 0;
            }

            return duration > 0 && (hasAudio || hasVideo)
                ? new AvInfo(TimeSpan.FromSeconds(duration), hasAudio, hasVideo, width, height)
                : null;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public float[]? DecodeAudio(string path, TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var asset = CreateAsset(path);
            if (asset == 0)
                return null;
            var tracks = Tracks(asset, "AVMediaTypeAudio");
            if (Count(tracks) == 0)
                return null;

            nint error = 0;
            var reader = Msg(Class("AVAssetReader"), "assetReaderWithAsset:error:", asset, (nint)(&error));
            if (reader == 0)
                return null;

            var keys = new[]
            {
                Constant(s_avFoundation, "AVFormatIDKey"),
                Constant(s_avFoundation, "AVSampleRateKey"),
                Constant(s_avFoundation, "AVNumberOfChannelsKey"),
                Constant(s_avFoundation, "AVLinearPCMBitDepthKey"),
                Constant(s_avFoundation, "AVLinearPCMIsFloatKey"),
                Constant(s_avFoundation, "AVLinearPCMIsBigEndianKey"),
                Constant(s_avFoundation, "AVLinearPCMIsNonInterleaved"),
            };
            var values = new[]
            {
                NumberUInt(AudioFormatLinearPcm),
                NumberDouble(AudioClip.SampleRate),
                NumberUInt(1),
                NumberUInt(32),
                NumberBool(true),
                NumberBool(false),
                NumberBool(false),
            };
            var settings = Dictionary(keys, values);
            var output = Msg(Class("AVAssetReaderAudioMixOutput"), "assetReaderAudioMixOutputWithAudioTracks:audioSettings:", tracks, settings);
            if (output == 0 || !MsgBool(reader, "canAddOutput:", output))
                return null;
            Msg(reader, "addOutput:", output);
            var range = new CMTimeRange { Start = CMTime.From(start), Duration = CMTime.From(length) };
            ((delegate* unmanaged<nint, nint, CMTimeRange, void>)s_msgSend)(reader, Selector("setTimeRange:"), range);
            if (!MsgBool(reader, "startReading"))
                return null;

            var samples = new List<float>((int)(length.TotalSeconds * AudioClip.SampleRate) + 1024);
            var max = (int)Math.Ceiling(length.TotalSeconds * AudioClip.SampleRate);
            try
            {
                while (samples.Count < max)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var buffer = Msg(output, "copyNextSampleBuffer");
                    if (buffer == 0)
                        break;
                    try
                    {
                        var block = CMSampleBufferGetDataBuffer(buffer);
                        if (block == 0)
                            continue;
                        var bytes = (int)CMBlockBufferGetDataLength(block);
                        var floats = new float[bytes / sizeof(float)];
                        fixed (float* p = floats)
                        {
                            if (CMBlockBufferCopyDataBytes(block, 0, (nuint)(floats.Length * sizeof(float)), p) != 0)
                                continue;
                        }

                        samples.AddRange(floats);
                    }
                    finally
                    {
                        CFRelease(buffer);
                    }
                }
            }
            finally
            {
                if (MsgLong(reader, "status") == AssetReaderStatusReading)
                    Msg(reader, "cancelReading");
            }

            return samples.Count == 0 ? null : samples.Take(max).ToArray();
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public IReadOnlyList<DecodedImage?>? DecodeFrames(string path, IReadOnlyList<TimeSpan> times, int width, int height, CancellationToken cancellationToken)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var asset = CreateAsset(path);
            if (asset == 0)
                return null;
            var generator = Msg(Class("AVAssetImageGenerator"), "assetImageGeneratorWithAsset:", asset);
            if (generator == 0)
                return null;
            MsgSetBool(generator, "setAppliesPreferredTrackTransform:", true);
            ((delegate* unmanaged<nint, nint, CGSize, void>)s_msgSend)(generator, Selector("setMaximumSize:"),
                new CGSize { Width = width, Height = height });
            var tolerance = CMTime.From(TimeSpan.FromMilliseconds(500));
            ((delegate* unmanaged<nint, nint, CMTime, void>)s_msgSend)(generator, Selector("setRequestedTimeToleranceBefore:"), tolerance);
            ((delegate* unmanaged<nint, nint, CMTime, void>)s_msgSend)(generator, Selector("setRequestedTimeToleranceAfter:"), tolerance);

            var frames = new List<DecodedImage?>(times.Count);
            var copyImage = Selector("copyCGImageAtTime:actualTime:error:");
            foreach (var time in times)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint error = 0;
                var image = ((delegate* unmanaged<nint, nint, CMTime, CMTime*, nint*, nint>)s_msgSend)(
                    generator, copyImage, CMTime.From(time), null, &error);
                if (image == 0)
                {
                    frames.Add(null);
                    continue;
                }

                try
                {
                    var rgb = ToRgb(image);
                    frames.Add(rgb is null
                        ? null
                        : rgb.Width == width && rgb.Height == height
                            ? rgb
                            : new DecodedImage(width, height, BicubicResampler.Resize(rgb.Rgb, rgb.Width, rgb.Height, width, height)));
                }
                finally
                {
                    CGImageRelease(image);
                }
            }

            return frames;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    // ------------------------------------------------------------------ pictures (ImageIO)

    public static bool CanDecodeImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Decodes a picture with ImageIO, upright and at most <paramref name="maxSide"/> pixels on its longest
    /// side.</summary>
    public static DecodedImage? DecodeImage(string path, int maxSide, CancellationToken cancellationToken)
    {
        if (!EnsureLoaded())
            return null;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var url = FileUrl(path);
            if (url == 0)
                return null;
            var source = CGImageSourceCreateWithURL(url, 0);
            if (source == 0)
                return null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var options = Dictionary(
                    [
                        Constant(s_imageIO, "kCGImageSourceCreateThumbnailFromImageAlways"),
                        Constant(s_imageIO, "kCGImageSourceCreateThumbnailWithTransform"),
                        Constant(s_imageIO, "kCGImageSourceThumbnailMaxPixelSize"),
                    ],
                    [NumberBool(true), NumberBool(true), NumberUInt((uint)maxSide)]);
                var image = CGImageSourceCreateThumbnailAtIndex(source, 0, options);
                if (image == 0)
                    return null;
                try
                {
                    return ToRgb(image);
                }
                finally
                {
                    CGImageRelease(image);
                }
            }
            finally
            {
                CFRelease(source);
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Draws a CGImage into an RGBX buffer (no colour conversion beyond what drawing requires) and drops X.</summary>
    private static DecodedImage? ToRgb(nint image)
    {
        var width = (int)CGImageGetWidth(image);
        var height = (int)CGImageGetHeight(image);
        if (width <= 0 || height <= 0 || (long)width * height > SkiaImageDecoder.MaxPixels)
            return null;
        var rgbx = new byte[width * height * 4];
        var space = CGColorSpaceCreateDeviceRGB();
        try
        {
            fixed (byte* p = rgbx)
            {
                var context = CGBitmapContextCreate(p, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, ImageAlphaNoneSkipLast);
                if (context == 0)
                    return null;
                try
                {
                    CGContextDrawImage(context, new CGRect { Width = width, Height = height }, image);
                }
                finally
                {
                    CGContextRelease(context);
                }
            }
        }
        finally
        {
            CGColorSpaceRelease(space);
        }

        var rgb = new byte[width * height * 3];
        for (int i = 0, o = 0; o < rgb.Length; i += 4, o += 3)
        {
            rgb[o] = rgbx[i];
            rgb[o + 1] = rgbx[i + 1];
            rgb[o + 2] = rgbx[i + 2];
        }

        return new DecodedImage(width, height, rgb);
    }

    // ------------------------------------------------------------------ Objective-C helpers

    private static nint CreateAsset(string path)
    {
        var url = FileUrl(path);
        return url == 0 ? 0 : Msg(Class("AVURLAsset"), "URLAssetWithURL:options:", url, (nint)0);
    }

    private static nint FileUrl(string path) => Msg(Class("NSURL"), "fileURLWithPath:", NSString(Path.GetFullPath(path)));

    private static nint Tracks(nint asset, string mediaTypeConstant) =>
        Msg(asset, "tracksWithMediaType:", Constant(s_avFoundation, mediaTypeConstant));

    private static long Count(nint array) => array == 0 ? 0 : MsgLong(array, "count");

    private static nint NSString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes)
            return Msg(Class("NSString"), "stringWithUTF8String:", (nint)p);
    }

    private static nint NumberUInt(uint value) =>
        ((delegate* unmanaged<nint, nint, uint, nint>)s_msgSend)(Class("NSNumber"), Selector("numberWithUnsignedInt:"), value);

    private static nint NumberDouble(double value) =>
        ((delegate* unmanaged<nint, nint, double, nint>)s_msgSend)(Class("NSNumber"), Selector("numberWithDouble:"), value);

    private static nint NumberBool(bool value) =>
        ((delegate* unmanaged<nint, nint, byte, nint>)s_msgSend)(Class("NSNumber"), Selector("numberWithBool:"), value ? (byte)1 : (byte)0);

    private static nint Dictionary(nint[] keys, nint[] values)
    {
        fixed (nint* k = keys)
        fixed (nint* v = values)
        {
            return ((delegate* unmanaged<nint, nint, nint*, nint*, nuint, nint>)s_msgSend)(
                Class("NSDictionary"), Selector("dictionaryWithObjects:forKeys:count:"), v, k, (nuint)keys.Length);
        }
    }

    /// <summary>Value of an exported Objective-C/CF constant (e.g. <c>AVMediaTypeAudio</c>, an <c>NSString *</c>).</summary>
    private static nint Constant(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);

    private static nint Class(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return objc_getClass(p);
    }

    private static nint Selector(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return sel_registerName(p);
    }

    private static nint Msg(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, Selector(selector));

    private static nint Msg(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Selector(selector), a);

    private static nint Msg(nint receiver, string selector, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, Selector(selector), a, b);

    private static long MsgLong(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, long>)s_msgSend)(receiver, Selector(selector));

    private static bool MsgBool(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(receiver, Selector(selector)) != 0;

    private static bool MsgBool(nint receiver, string selector, nint a) =>
        ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(receiver, Selector(selector), a) != 0;

    private static void MsgSetBool(nint receiver, string selector, bool value) =>
        ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, Selector(selector), value ? (byte)1 : (byte)0);

    private static CMTime MsgCMTime(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, CMTime>)s_msgSendStret)(receiver, Selector(selector));

    private static CGSize MsgSize(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, CGSize>)s_msgSend)(receiver, Selector(selector));

    private static CGAffineTransform MsgTransform(nint receiver, string selector) =>
        ((delegate* unmanaged<nint, nint, CGAffineTransform>)s_msgSendStret)(receiver, Selector(selector));

    [LibraryImport(Objc)]
    private static partial nint objc_getClass(byte* name);

    [LibraryImport(Objc)]
    private static partial nint sel_registerName(byte* name);

    [LibraryImport(Objc)]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport(Objc)]
    private static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    [LibraryImport(CoreMedia)]
    private static partial nint CMSampleBufferGetDataBuffer(nint buffer);

    [LibraryImport(CoreMedia)]
    private static partial nuint CMBlockBufferGetDataLength(nint buffer);

    [LibraryImport(CoreMedia)]
    private static partial int CMBlockBufferCopyDataBytes(nint buffer, nuint offset, nuint length, void* destination);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetWidth(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nuint CGImageGetHeight(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGImageRelease(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextDrawImage(nint context, CGRect rect, nint image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextRelease(nint context);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateWithURL(nint url, nint options);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateThumbnailAtIndex(nint source, nuint index, nint options);
}
