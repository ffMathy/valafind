using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Valafind.Core.Platform;

/// <summary>Detects macOS "dataless" files and directories (iCloud Drive / File Provider placeholders whose
/// content would be downloaded on access) via lstat st_flags &amp; SF_DATALESS.</summary>
[SupportedOSPlatform("macos")]
internal static partial class MacFileFlags
{
    private const uint SfDataless = 0x40000000;
    // struct stat (64-bit inode layout): st_flags lives at byte offset 116 of a 144-byte struct on x64 and arm64.
    private const int StFlagsOffset = 116;
    private const int StatBufferSize = 256;
    private static bool s_unavailable;

    public static unsafe bool IsDataless(string path)
    {
        if (s_unavailable)
            return false;
        try
        {
            var buffer = stackalloc byte[StatBufferSize];
            var rc = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? lstat_inode64(path, buffer)
                : lstat(path, buffer);
            if (rc != 0)
                return false;
            var flags = *(uint*)(buffer + StFlagsOffset);
            return (flags & SfDataless) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            s_unavailable = true;
            return false;
        }
    }

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int lstat(string path, byte* buffer);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int lstat_inode64(string path, byte* buffer);
}
