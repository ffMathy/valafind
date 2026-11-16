using System.Globalization;

namespace Valafind.Core.Platform;

/// <summary>Aggregate CPU jiffies from the first "cpu" line of /proc/stat.</summary>
internal readonly record struct CpuTimes(ulong Busy, ulong Total);

/// <summary>Cumulative counters of one block device from /proc/diskstats.</summary>
internal readonly record struct DiskCounters(string Name, ulong SectorsRead, ulong SectorsWritten, ulong IoTicksMs);

/// <summary>One entry of /sys/class/power_supply.</summary>
internal readonly record struct PowerSupplyInfo(string Type, bool? Online, string? Status, string? Scope);

/// <summary>Pure parsers for Linux procfs / sysfs text so they can be unit-tested with sample input.</summary>
internal static class LinuxProcParsers
{
    private const int SectorBytes = 512;

    /// <summary>Parses the aggregate "cpu " line. Busy = user+nice+system+irq+softirq+steal; total adds idle+iowait.
    /// guest/guest_nice are already included in user/nice and are therefore ignored.</summary>
    public static bool TryParseCpu(string procStat, out CpuTimes times)
    {
        times = default;
        foreach (var rawLine in procStat.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("cpu ", StringComparison.Ordinal))
                continue;

            Span<ulong> values = stackalloc ulong[8];
            var count = 0;
            foreach (var range in line[3..].Split(' '))
            {
                var token = line[3..][range];
                if (token.IsEmpty)
                    continue;
                if (count >= values.Length)
                    break;
                if (!ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out values[count]))
                    return false;
                count++;
            }
            if (count < 4)
                return false;

            // user nice system idle iowait irq softirq steal
            var busy = values[0] + values[1] + values[2] + values[5] + values[6] + values[7];
            var idle = values[3] + values[4];
            times = new CpuTimes(busy, busy + idle);
            return true;
        }
        return false;
    }

    /// <summary>CPU utilisation (0–100) between two snapshots.</summary>
    public static double CpuPercent(CpuTimes previous, CpuTimes current)
    {
        if (current.Total <= previous.Total || current.Busy < previous.Busy)
            return 0;
        var total = (double)(current.Total - previous.Total);
        var busy = (double)(current.Busy - previous.Busy);
        return Math.Clamp(busy / total * 100.0, 0, 100);
    }

    /// <summary>Parses /proc/diskstats, keeping devices for which <paramref name="include"/> returns true.</summary>
    public static List<DiskCounters> ParseDiskStats(string text, Func<string, bool> include)
    {
        var result = new List<DiskCounters>();
        foreach (var rawLine in text.Split('\n'))
        {
            var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // major minor name rd rd_merged sectors_rd ms_rd wr wr_merged sectors_wr ms_wr in_flight io_ticks ...
            if (parts.Length < 13)
                continue;
            var name = parts[2];
            if (!include(name))
                continue;
            if (!ulong.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var sectorsRead) ||
                !ulong.TryParse(parts[9], NumberStyles.None, CultureInfo.InvariantCulture, out var sectorsWritten) ||
                !ulong.TryParse(parts[12], NumberStyles.None, CultureInfo.InvariantCulture, out var ioTicks))
                continue;
            result.Add(new DiskCounters(name, sectorsRead, sectorsWritten, ioTicks));
        }
        return result;
    }

    /// <summary>Busy percent (max over disks) and total bytes/s between two snapshots.</summary>
    public static (double BusyPercent, double BytesPerSecond) DiskDelta(
        IReadOnlyDictionary<string, DiskCounters> previous, IEnumerable<DiskCounters> current, double elapsedMs)
    {
        if (elapsedMs <= 0)
            return (0, 0);
        double maxBusy = 0, bytes = 0;
        foreach (var disk in current)
        {
            if (!previous.TryGetValue(disk.Name, out var prev))
                continue;
            var ticks = Delta(prev.IoTicksMs, disk.IoTicksMs);
            maxBusy = Math.Max(maxBusy, ticks / elapsedMs * 100.0);
            bytes += (Delta(prev.SectorsRead, disk.SectorsRead) + Delta(prev.SectorsWritten, disk.SectorsWritten)) * (double)SectorBytes;
        }
        return (Math.Clamp(maxBusy, 0, 100), bytes / (elapsedMs / 1000.0));
    }

    private static double Delta(ulong previous, ulong current) => current >= previous ? current - previous : 0;

    /// <summary>Names of virtual / non-disk block devices that never count as physical disks.</summary>
    public static bool IsVirtualDeviceName(string name) =>
        name.StartsWith("loop", StringComparison.Ordinal) ||
        name.StartsWith("ram", StringComparison.Ordinal) ||
        name.StartsWith("zram", StringComparison.Ordinal) ||
        name.StartsWith("dm-", StringComparison.Ordinal) ||
        name.StartsWith("md", StringComparison.Ordinal) ||
        name.StartsWith("sr", StringComparison.Ordinal) ||
        name.StartsWith("fd", StringComparison.Ordinal) ||
        name.StartsWith("nbd", StringComparison.Ordinal);

    /// <summary>True when the machine runs on battery: a system battery is discharging and no mains/USB
    /// supply reports being online. Device-scoped batteries (mice, keyboards) are ignored. No supplies → false.</summary>
    public static bool IsOnBattery(IEnumerable<PowerSupplyInfo> supplies)
    {
        var anyAdapterOnline = false;
        var discharging = false;
        foreach (var s in supplies)
        {
            if (string.Equals(s.Scope, "Device", StringComparison.OrdinalIgnoreCase))
                continue;
            if (s.Type.Equals("Mains", StringComparison.OrdinalIgnoreCase) ||
                s.Type.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
            {
                if (s.Online == true)
                    anyAdapterOnline = true;
            }
            else if (s.Type.Equals("Battery", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(s.Status?.Trim(), "Discharging", StringComparison.OrdinalIgnoreCase))
            {
                discharging = true;
            }
        }
        return discharging && !anyAdapterOnline;
    }

    /// <summary>Parses read_bytes + write_bytes (actual storage I/O) from /proc/self/io.</summary>
    public static long? ParseProcessIo(string text)
    {
        long? read = null, write = null;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            var key = line.AsSpan(0, colon).Trim();
            var value = line.AsSpan(colon + 1).Trim();
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                continue;
            if (key.SequenceEqual("read_bytes"))
                read = n;
            else if (key.SequenceEqual("write_bytes"))
                write = n;
        }
        return read is null && write is null ? null : (read ?? 0) + (write ?? 0);
    }
}
