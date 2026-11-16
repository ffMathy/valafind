using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public class LinuxProcParsersTests
{
    private const string ProcStat1 = """
        cpu  1000 50 300 8000 200 10 20 5 0 0
        cpu0 500 25 150 4000 100 5 10 2 0 0
        intr 12345
        """;

    private const string ProcStat2 = """
        cpu  1300 50 400 8400 300 10 20 5 0 0
        cpu0 650 25 200 4200 150 5 10 2 0 0
        """;

    [Fact]
    public void ParsesAggregateCpuLine()
    {
        Assert.True(LinuxProcParsers.TryParseCpu(ProcStat1, out var t));
        // busy = user+nice+system+irq+softirq+steal; idle+iowait added for total
        Assert.Equal(1000UL + 50 + 300 + 10 + 20 + 5, t.Busy);
        Assert.Equal(t.Busy + 8000 + 200, t.Total);
    }

    [Fact]
    public void CpuPercentFromDeltas()
    {
        Assert.True(LinuxProcParsers.TryParseCpu(ProcStat1, out var a));
        Assert.True(LinuxProcParsers.TryParseCpu(ProcStat2, out var b));
        // Δbusy = 300 + 100 = 400; Δidle = 400 + 100 (iowait) = 500 → 400 / 900
        Assert.Equal(400.0 / 900.0 * 100.0, LinuxProcParsers.CpuPercent(a, b), 6);
        Assert.Equal(0, LinuxProcParsers.CpuPercent(b, a));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4")]
    [InlineData("cpu  a b c d")]
    [InlineData("cpu  1 2")]
    public void RejectsMalformedProcStat(string text) => Assert.False(LinuxProcParsers.TryParseCpu(text, out _));

    private const string DiskStats1 = """
           7       0 loop0 100 0 2000 10 0 0 0 0 0 50 10 0 0 0 0
         259       0 nvme0n1 1000 10 20000 500 2000 20 40000 900 0 1000 1400 0 0 0 0
         259       1 nvme0n1p1 900 10 18000 450 1900 20 38000 850 0 950 1300 0 0 0 0
           8       0 sda 10 0 80 5 10 0 80 5 0 100 10
         253       0 dm-0 1000 0 20000 500 2000 0 40000 900 0 1000 1400 0 0 0 0
        """;

    private const string DiskStats2 = """
           7       0 loop0 100 0 2000 10 0 0 0 0 0 950 10 0 0 0 0
         259       0 nvme0n1 1100 10 22000 550 2100 20 44000 950 0 1300 1500 0 0 0 0
         259       1 nvme0n1p1 1000 10 20000 500 2000 20 42000 900 0 1250 1400 0 0 0 0
           8       0 sda 10 0 80 5 10 0 80 5 0 600 10
         253       0 dm-0 1000 0 20000 500 2000 0 40000 900 0 1900 1400 0 0 0 0
        """;

    private static bool WholeDisks(string name) => name is "nvme0n1" or "sda";

    [Fact]
    public void ParsesDiskStatsKeepingIncludedDevicesOnly()
    {
        var disks = LinuxProcParsers.ParseDiskStats(DiskStats1, WholeDisks);
        Assert.Equal(["nvme0n1", "sda"], disks.Select(d => d.Name));
        var nvme = disks[0];
        Assert.Equal(20000UL, nvme.SectorsRead);
        Assert.Equal(40000UL, nvme.SectorsWritten);
        Assert.Equal(1000UL, nvme.IoTicksMs);
    }

    [Fact]
    public void DiskDeltaTakesMaxBusyAndSumsBytes()
    {
        var before = LinuxProcParsers.ParseDiskStats(DiskStats1, WholeDisks).ToDictionary(d => d.Name);
        var after = LinuxProcParsers.ParseDiskStats(DiskStats2, WholeDisks);
        var (busy, bytes) = LinuxProcParsers.DiskDelta(before, after, elapsedMs: 1000);
        // nvme: 300 ms of 1000 → 30 %; sda: 500 ms → 50 % (max wins)
        Assert.Equal(50, busy, 6);
        // nvme: (2000 + 4000) sectors × 512 over 1 s; sda unchanged
        Assert.Equal(6000 * 512.0, bytes, 3);
    }

    [Fact]
    public void DiskDeltaClampsAndHandlesCounterReset()
    {
        var before = new Dictionary<string, DiskCounters> { ["sda"] = new("sda", 100, 100, 5000) };
        var (busy, bytes) = LinuxProcParsers.DiskDelta(before, [new DiskCounters("sda", 10, 10, 100)], 1000);
        Assert.Equal(0, busy);
        Assert.Equal(0, bytes);
        var (busy2, _) = LinuxProcParsers.DiskDelta(before, [new DiskCounters("sda", 100, 100, 9000)], 1000);
        Assert.Equal(100, busy2);
    }

    [Theory]
    [InlineData("loop3", true)]
    [InlineData("ram0", true)]
    [InlineData("zram0", true)]
    [InlineData("dm-1", true)]
    [InlineData("md127", true)]
    [InlineData("sr0", true)]
    [InlineData("sda", false)]
    [InlineData("nvme0n1", false)]
    [InlineData("vda", false)]
    [InlineData("mmcblk0", false)]
    public void VirtualDeviceNames(string name, bool expected) =>
        Assert.Equal(expected, LinuxProcParsers.IsVirtualDeviceName(name));

    private static readonly (PowerSupplyInfo[] Supplies, bool Expected)[] BatteryCases =
    [
        ([], false),
        ([new("Mains", false, null, null), new("Battery", null, "Discharging", null)], true),
        ([new("Mains", true, null, null), new("Battery", null, "Charging", null)], false),
        ([new("Mains", true, null, null), new("Battery", null, "Discharging", null)], false),
        ([new("Battery", null, "Discharging", null)], true),
        ([new("Mains", false, null, null), new("Battery", null, "Full", null)], false),
        ([new("USB", false, null, null), new("Battery", null, "Discharging\n", null)], true),
        // A wireless mouse battery must not count.
        ([new("Mains", true, null, null), new("Battery", null, "Discharging", "Device")], false),
        ([new("Mains", false, null, null), new("Battery", null, "Discharging", "Device")], false),
    ];

    [Fact]
    public void BatteryDetection()
    {
        for (var i = 0; i < BatteryCases.Length; i++)
        {
            var (supplies, expected) = BatteryCases[i];
            Assert.True(expected == LinuxProcParsers.IsOnBattery(supplies), $"case {i}");
        }
    }

    [Fact]
    public void ParsesProcSelfIo()
    {
        const string text = """
            rchar: 323934931
            wchar: 323929600
            syscr: 632687
            syscw: 632675
            read_bytes: 4096
            write_bytes: 323932160
            cancelled_write_bytes: 0
            """;
        Assert.Equal(4096L + 323932160L, LinuxProcParsers.ParseProcessIo(text));
        Assert.Null(LinuxProcParsers.ParseProcessIo("rchar: 1\n"));
    }

    [Theory]
    [InlineData(2.0, 4, 50.0)]
    [InlineData(8.0, 4, 100.0)]
    [InlineData(0.0, 4, 0.0)]
    [InlineData(1.0, 0, 100.0)]
    public void LoadAverageToPercent(double load, int cpus, double expected) =>
        Assert.Equal(expected, LoadAverageMonitor.LoadToPercent(load, cpus), 6);
}
