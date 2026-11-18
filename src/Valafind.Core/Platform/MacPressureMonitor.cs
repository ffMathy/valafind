using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>macOS implementation: host_statistics(HOST_CPU_LOAD_INFO) for CPU, IOKit IOBlockStorageDriver
/// "Statistics" for disk, IOKit IOAccelerator "PerformanceStatistics" for the GPU, IOPowerSources for battery.</summary>
/// <remarks>CoreFoundation ownership: objects returned by *Create* / *Copy* functions are released here;
/// objects obtained through *Get* functions are borrowed and never released.</remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacPressureMonitor : ISystemPressureMonitor
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int HostCpuLoadInfo = 3;
    private const int CpuStateUser = 0, CpuStateSystem = 1, CpuStateIdle = 2, CpuStateNice = 3;
    private const uint KCFStringEncodingUtf8 = 0x08000100;
    private const int KCFNumberSInt64Type = 4;
    private static readonly TimeSpan BaselineDelay = TimeSpan.FromMilliseconds(250);

    private static uint s_hostPort;

    private readonly Lock _lock = new();
    private readonly nint _keyStatistics;
    private readonly nint _keyBytesRead;
    private readonly nint _keyBytesWritten;
    private readonly nint _keyTimeRead;
    private readonly nint _keyTimeWritten;
    private readonly nint _keyPerformance;
    private readonly nint _keyGpuUtilisation;
    private bool _gpuFailed;
    private CpuTimes? _previousCpu;
    private Dictionary<ulong, DiskStat>? _previousDisks;
    private long _previousTimestamp;
    private bool _diskFailed;
    private bool _disposed;

    private readonly record struct DiskStat(long Bytes, long TimeNs);

    public MacPressureMonitor()
    {
        try
        {
            _keyStatistics = CreateString("Statistics");
            _keyBytesRead = CreateString("Bytes (Read)");
            _keyBytesWritten = CreateString("Bytes (Write)");
            _keyTimeRead = CreateString("Total Time (Read)");
            _keyTimeWritten = CreateString("Total Time (Write)");
            _keyPerformance = CreateString("PerformanceStatistics");
            _keyGpuUtilisation = CreateString("Device Utilization %");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _diskFailed = true;
        }
        _gpuFailed = _keyPerformance == 0 || _keyGpuUtilisation == 0;
        if (_keyStatistics == 0 || _keyBytesRead == 0 || _keyBytesWritten == 0 || _keyTimeRead == 0 || _keyTimeWritten == 0)
            _diskFailed = true;
    }

    public PressureSample Sample()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previousCpu is null)
            {
                _previousCpu = ReadCpu();
                _previousDisks = ReadDisks();
                _previousTimestamp = Stopwatch.GetTimestamp();
                Thread.Sleep(BaselineDelay);
            }

            var cpu = ReadCpu();
            var disks = ReadDisks();
            var now = Stopwatch.GetTimestamp();
            var elapsedNs = Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalMilliseconds * 1_000_000.0;

            var cpuPercent = cpu is { } c && _previousCpu is { } p ? LinuxProcParsers.CpuPercent(p, c) : 0;

            double? busy = null, bytes = null;
            if (disks is not null && _previousDisks is not null && elapsedNs > 0)
            {
                double maxBusy = 0, totalBytes = 0;
                foreach (var (id, stat) in disks)
                {
                    if (!_previousDisks.TryGetValue(id, out var prev))
                        continue;
                    var dt = Math.Max(0, stat.TimeNs - prev.TimeNs);
                    maxBusy = Math.Max(maxBusy, dt / elapsedNs * 100.0);
                    totalBytes += Math.Max(0, stat.Bytes - prev.Bytes);
                }
                busy = Math.Clamp(maxBusy, 0, 100);
                bytes = totalBytes / (elapsedNs / 1_000_000_000.0);
            }

            if (cpu is not null)
                _previousCpu = cpu;
            _previousDisks = disks;
            _previousTimestamp = now;
            return new PressureSample(DateTimeOffset.UtcNow, cpuPercent, busy, bytes, ReadOnBattery(), ReadGpu());
        }
    }

    private static unsafe CpuTimes? ReadCpu()
    {
        try
        {
            if (s_hostPort == 0)
                s_hostPort = mach_host_self();
            var ticks = stackalloc uint[4];
            uint count = 4;
            if (host_statistics(s_hostPort, HostCpuLoadInfo, ticks, ref count) != 0 || count < 4)
                return null;
            ulong busy = (ulong)ticks[CpuStateUser] + ticks[CpuStateSystem] + ticks[CpuStateNice];
            return new CpuTimes(busy, busy + ticks[CpuStateIdle]);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads per-driver statistics keyed by registry entry id. Returns null on any failure.</summary>
    private Dictionary<ulong, DiskStat>? ReadDisks()
    {
        if (_diskFailed)
            return null;
        try
        {
            // IOServiceGetMatchingServices consumes the matching dictionary reference, even on failure.
            var matching = IOServiceMatching("IOBlockStorageDriver");
            if (matching == 0)
                return null;
            if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
                return null;
            var result = new Dictionary<ulong, DiskStat>();
            try
            {
                uint entry;
                while ((entry = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        if (IORegistryEntryGetRegistryEntryID(entry, out var id) != 0)
                            continue;
                        var stats = IORegistryEntryCreateCFProperty(entry, _keyStatistics, 0, 0);
                        if (stats == 0)
                            continue;
                        try
                        {
                            if (CFGetTypeID(stats) != CFDictionaryGetTypeID())
                                continue;
                            var bytes = GetInt64(stats, _keyBytesRead) + GetInt64(stats, _keyBytesWritten);
                            var time = GetInt64(stats, _keyTimeRead) + GetInt64(stats, _keyTimeWritten);
                            result[id] = new DiskStat(bytes, time);
                        }
                        finally
                        {
                            CFRelease(stats);
                        }
                    }
                    finally
                    {
                        _ = IOObjectRelease(entry);
                    }
                }
            }
            finally
            {
                _ = IOObjectRelease(iterator);
            }
            return result;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _diskFailed = true;
            return null;
        }
    }

    /// <summary>Busiest GPU's "Device Utilization %" (what Activity Monitor's GPU history shows); null on failure.</summary>
    private double? ReadGpu()
    {
        if (_gpuFailed)
            return null;
        try
        {
            var matching = IOServiceMatching("IOAccelerator"); // consumed below
            if (matching == 0 || IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
                return null;
            double? max = null;
            try
            {
                uint entry;
                while ((entry = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        var stats = IORegistryEntryCreateCFProperty(entry, _keyPerformance, 0, 0);
                        if (stats == 0)
                            continue;
                        try
                        {
                            if (CFGetTypeID(stats) != CFDictionaryGetTypeID())
                                continue;
                            var value = CFDictionaryGetValue(stats, _keyGpuUtilisation); // borrowed
                            if (value != 0 && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, KCFNumberSInt64Type, out var percent))
                                max = Math.Max(max ?? 0, Math.Clamp(percent, 0, 100));
                        }
                        finally
                        {
                            CFRelease(stats);
                        }
                    }
                    finally
                    {
                        _ = IOObjectRelease(entry);
                    }
                }
            }
            finally
            {
                _ = IOObjectRelease(iterator);
            }
            return max;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            _gpuFailed = true;
            return null;
        }
    }

    private static long GetInt64(nint dictionary, nint key)
    {
        var value = CFDictionaryGetValue(dictionary, key); // borrowed
        if (value == 0 || CFGetTypeID(value) != CFNumberGetTypeID())
            return 0;
        return CFNumberGetValue(value, KCFNumberSInt64Type, out var n) ? n : 0;
    }

    private static unsafe bool ReadOnBattery()
    {
        try
        {
            var info = IOPSCopyPowerSourcesInfo(); // owned
            if (info == 0)
                return false;
            try
            {
                var type = IOPSGetProvidingPowerSourceType(info); // borrowed
                if (type == 0 || CFGetTypeID(type) != CFStringGetTypeID())
                    return false;
                var buffer = stackalloc byte[64];
                if (!CFStringGetCString(type, buffer, 64, KCFStringEncodingUtf8))
                    return false;
                return Marshal.PtrToStringUTF8((nint)buffer) == "Battery Power";
            }
            finally
            {
                CFRelease(info);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static nint CreateString(string value) => CFStringCreateWithCString(0, value, KCFStringEncodingUtf8);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var key in new[] { _keyStatistics, _keyBytesRead, _keyBytesWritten, _keyTimeRead, _keyTimeWritten, _keyPerformance, _keyGpuUtilisation })
            {
                if (key != 0)
                    CFRelease(key);
            }
        }
    }

    [LibraryImport(LibSystem)]
    private static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    private static unsafe partial int host_statistics(uint host, int flavor, uint* info, ref uint count);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(nint number, int type, out long value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool CFStringGetCString(nint value, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint cf);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    private static partial int IOServiceGetMatchingServices(uint mainPort, nint matching, out uint iterator);

    [LibraryImport(IOKit)]
    private static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKit)]
    private static partial int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);

    [LibraryImport(IOKit)]
    private static partial nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);

    [LibraryImport(IOKit)]
    private static partial nint IOPSCopyPowerSourcesInfo();

    [LibraryImport(IOKit)]
    private static partial nint IOPSGetProvidingPowerSourceType(nint snapshot);
}
