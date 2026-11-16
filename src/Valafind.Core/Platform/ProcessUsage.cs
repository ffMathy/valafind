using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Valafind.Core.Platform;

/// <summary>Cumulative resource usage of the current process, used to subtract Valafind's own load from
/// machine-wide pressure so indexing does not throttle itself.</summary>
internal readonly record struct OwnUsage(TimeSpan CpuTime, long? IoBytes);

internal interface IOwnUsageProbe
{
    OwnUsage Read();
}

/// <summary>Reads the current process' CPU time and (where cheap) storage I/O byte counters.</summary>
internal sealed partial class ProcessUsageProbe : IOwnUsageProbe
{
    private readonly Process _process = Process.GetCurrentProcess();
    private bool _ioUnavailable;

    public OwnUsage Read()
    {
        TimeSpan cpu;
        try
        {
            _process.Refresh();
            cpu = _process.TotalProcessorTime;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            cpu = TimeSpan.Zero;
        }
        return new OwnUsage(cpu, _ioUnavailable ? null : ReadIoBytes());
    }

    private long? ReadIoBytes()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return SafeFile.TryReadAllText("/proc/self/io") is { } text ? LinuxProcParsers.ParseProcessIo(text) : null;
            if (OperatingSystem.IsWindows())
                return GetProcessIoCounters(GetCurrentProcess(), out var c) ? (long)(c.ReadTransferCount + c.WriteTransferCount) : null;
            if (OperatingSystem.IsMacOS())
                return ReadMacDiskIo();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _ioUnavailable = true;
        }
        return null;
    }

    /// <summary>proc_pid_rusage(RUSAGE_INFO_V2): ri_diskio_bytesread / ri_diskio_byteswritten live at byte
    /// offsets 144 / 152 of struct rusage_info_v2 (160 bytes).</summary>
    private static unsafe long? ReadMacDiskIo()
    {
        var buffer = stackalloc byte[256];
        if (proc_pid_rusage(Environment.ProcessId, 2, buffer) != 0)
            return null;
        var read = *(ulong*)(buffer + 144);
        var written = *(ulong*)(buffer + 152);
        return (long)(read + written);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(nint process, out IoCounters counters);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static unsafe partial int proc_pid_rusage(int pid, int flavor, void* buffer);
}
