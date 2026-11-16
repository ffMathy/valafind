using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>Windows implementation: GetSystemTimes for CPU, PDH PhysicalDisk(_Total) counters for disk and
/// GetSystemPowerStatus for battery.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPressureMonitor : ISystemPressureMonitor
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint ErrorSuccess = 0;
    private static readonly TimeSpan BaselineDelay = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private nint _query;
    private nint _idleCounter;
    private nint _bytesCounter;
    private bool _pdhAvailable;
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

            double? busy = null, bytes = null;
            if (_pdhAvailable && PdhCollectQueryData(_query) == ErrorSuccess)
            {
                if (TryGetDouble(_idleCounter, PdhFmtDouble | PdhFmtNoCap100, out var idlePercent))
                    busy = Math.Clamp(100.0 - idlePercent, 0, 100);
                if (TryGetDouble(_bytesCounter, PdhFmtDouble | PdhFmtNoCap100, out var bps))
                    bytes = Math.Max(0, bps);
            }

            return new PressureSample(DateTimeOffset.UtcNow, cpu, busy, bytes, ReadOnBattery());
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

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
