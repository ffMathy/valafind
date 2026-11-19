using System.Diagnostics;
using System.Runtime.Versioning;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>Linux implementation based on /proc/stat, /proc/diskstats, /sys/class/power_supply and
/// <see cref="LinuxGpuUsage"/> for the GPU.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxPressureMonitor : ISystemPressureMonitor
{
    internal const string ProcStatPath = "/proc/stat";
    private const string DiskStatsPath = "/proc/diskstats";
    private const string PowerSupplyPath = "/sys/class/power_supply";
    private static readonly TimeSpan BaselineDelay = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, bool> _physicalCache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly LinuxGpuUsage _gpuUsage = new();
    private readonly GpuSampler _gpu;

    public LinuxPressureMonitor()
    {
        _gpu = new GpuSampler(_gpuUsage.Read);
    }
    private CpuTimes? _previousCpu;
    private Dictionary<string, DiskCounters>? _previousDisks;
    private long _previousTimestamp;

    public PressureSample Sample()
    {
        lock (_lock)
        {
            if (_previousCpu is null)
            {
                TakeSnapshot(out var cpu0, out var disks0);
                _previousCpu = cpu0;
                _previousDisks = disks0;
                _previousTimestamp = Stopwatch.GetTimestamp();
                Thread.Sleep(BaselineDelay);
            }

            TakeSnapshot(out var cpu, out var disks);
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalMilliseconds;

            var cpuPercent = cpu is { } c && _previousCpu is { } p ? LinuxProcParsers.CpuPercent(p, c) : 0;
            double? busy = null, bytes = null;
            if (disks is not null && _previousDisks is not null)
            {
                var (b, bps) = LinuxProcParsers.DiskDelta(_previousDisks, disks.Values, elapsedMs);
                busy = b;
                bytes = bps;
            }

            if (cpu is not null)
                _previousCpu = cpu;
            _previousDisks = disks;
            _previousTimestamp = now;

            return new PressureSample(DateTimeOffset.UtcNow, cpuPercent, busy, bytes, ReadOnBattery(), _gpu.Read());
        }
    }

    private void TakeSnapshot(out CpuTimes? cpu, out Dictionary<string, DiskCounters>? disks)
    {
        cpu = SafeFile.TryReadAllText(ProcStatPath) is { } stat && LinuxProcParsers.TryParseCpu(stat, out var t) ? t : null;
        disks = null;
        if (SafeFile.TryReadAllText(DiskStatsPath) is { } diskText)
        {
            disks = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
            foreach (var d in LinuxProcParsers.ParseDiskStats(diskText, IsPhysicalDisk))
                disks[d.Name] = d;
        }
    }

    /// <summary>Whole physical disks only: virtual names are skipped, partitions have a "partition" file and
    /// virtual devices have no "device" link in sysfs.</summary>
    private bool IsPhysicalDisk(string name)
    {
        if (_physicalCache.TryGetValue(name, out var cached))
            return cached;
        var result = false;
        if (!LinuxProcParsers.IsVirtualDeviceName(name))
        {
            var sysName = name.Replace('/', '!');
            try
            {
                result = !File.Exists($"/sys/class/block/{sysName}/partition") &&
                         Directory.Exists($"/sys/block/{sysName}/device");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                result = false;
            }
        }
        _physicalCache[name] = result;
        return result;
    }

    private static bool ReadOnBattery()
    {
        try
        {
            if (!Directory.Exists(PowerSupplyPath))
                return false;
            var supplies = new List<PowerSupplyInfo>();
            foreach (var dir in Directory.EnumerateDirectories(PowerSupplyPath))
            {
                var type = SafeFile.TryReadAllText(Path.Combine(dir, "type"))?.Trim();
                if (string.IsNullOrEmpty(type))
                    continue;
                var onlineText = SafeFile.TryReadAllText(Path.Combine(dir, "online"))?.Trim();
                bool? online = onlineText is null ? null : onlineText == "1";
                supplies.Add(new PowerSupplyInfo(type, online,
                    SafeFile.TryReadAllText(Path.Combine(dir, "status"))?.Trim(),
                    SafeFile.TryReadAllText(Path.Combine(dir, "scope"))?.Trim()));
            }
            return LinuxProcParsers.IsOnBattery(supplies);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _gpu.Dispose(); // first: it reads _gpuUsage
            _gpuUsage.Dispose();
        }
    }
}
