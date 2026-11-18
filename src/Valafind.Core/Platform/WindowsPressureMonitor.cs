using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>Windows implementation: GetSystemTimes for CPU, PDH PhysicalDisk(_Total) counters for disk, PDH
/// "GPU Engine(*)" counters for the GPU (Windows 10 1709 and later) and GetSystemPowerStatus for battery.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPressureMonitor : ISystemPressureMonitor
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private static readonly TimeSpan BaselineDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private nint _query;
    private nint _idleCounter;
    private nint _bytesCounter;
    private nint _gpuCounter;
    private bool _pdhAvailable;
    private readonly int _processId = Environment.ProcessId;
    private (ulong Idle, ulong Kernel, ulong User)? _previousTimes;
    private bool _disposed;

    public WindowsPressureMonitor()
    {
        try
        {
            if (PdhOpenQuery(null, 0, out _query) == ErrorSuccess &&
                PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", 0, out _idleCounter) == ErrorSuccess &&
                PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Bytes/sec", 0, out _bytesCounter) == ErrorSuccess)
            {
                // Optional: older Windows versions and some drivers have no GPU counters.
                if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuCounter) != ErrorSuccess)
                    _gpuCounter = 0;
                _pdhAvailable = PdhCollectQueryData(_query) == ErrorSuccess;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _pdhAvailable = false;
        }
        if (!_pdhAvailable)
            CloseQuery();
    }

    public PressureSample Sample()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_previousTimes is null)
            {
                _previousTimes = ReadTimes();
                Thread.Sleep(BaselineDelay);
            }

            var times = ReadTimes();
            double cpu = 0;
            if (times is { } cur && _previousTimes is { } prev)
            {
                // Kernel time includes idle time.
                var idle = Delta(prev.Idle, cur.Idle);
                var kernel = Delta(prev.Kernel, cur.Kernel);
                var user = Delta(prev.User, cur.User);
                var total = kernel + user;
                if (total > 0)
                    cpu = Math.Clamp((total - Math.Min(idle, total)) / total * 100.0, 0, 100);
            }
            if (times is not null)
                _previousTimes = times;

            double? busy = null, bytes = null, gpu = null, ownGpu = null;
            if (_pdhAvailable && PdhCollectQueryData(_query) == ErrorSuccess)
            {
                if (TryGetDouble(_idleCounter, PdhFmtDouble | PdhFmtNoCap100, out var idlePercent))
                    busy = Math.Clamp(100.0 - idlePercent, 0, 100);
                if (TryGetDouble(_bytesCounter, PdhFmtDouble | PdhFmtNoCap100, out var bps))
                    bytes = Math.Max(0, bps);
                if (_gpuCounter != 0)
                    (gpu, ownGpu) = GpuEngineMath.Aggregate(ReadArray(_gpuCounter), _processId);
            }

            return new PressureSample(DateTimeOffset.UtcNow, cpu, busy, bytes, ReadOnBattery(), gpu, ownGpu);
        }
    }

    private static double Delta(ulong previous, ulong current) => current >= previous ? current - previous : 0;

    private static (ulong Idle, ulong Kernel, ulong User)? ReadTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : null;

    private static bool TryGetDouble(nint counter, uint format, out double value)
    {
        value = 0;
        if (PdhGetFormattedCounterValue(counter, format, out _, out var v) != ErrorSuccess)
            return false;
        // CStatus: PDH_CSTATUS_VALID_DATA (0) or PDH_CSTATUS_NEW_DATA (1).
        if (v.CStatus > 1)
            return false;
        value = v.DoubleValue;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>All instances of a wildcard counter with their values; empty on failure.</summary>
    private static unsafe List<(string Name, double Percent)> ReadArray(nint counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, null) != PdhMoreData || size == 0)
            return result;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, p) != ErrorSuccess)
                return result;
            var items = (PdhFmtCounterValueItem*)p;
            for (var i = 0; i < count; i++)
            {
                if (items[i].Value.CStatus > 1 || items[i].Name == 0)
                    continue;
                if (Marshal.PtrToStringUni(items[i].Name) is { } name)
                    result.Add((name, items[i].Value.DoubleValue));
            }
        }
        return result;
    }

    private static bool ReadOnBattery() =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 0 && (status.BatteryFlag & 128) == 0;

    private void CloseQuery()
    {
        if (_query != 0)
        {
            try
            {
                _ = PdhCloseQuery(_query);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            _query = 0;
        }
        _pdhAvailable = false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseQuery();
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string counterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static unsafe partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
