using System.Globalization;
using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public class SystemPressureMonitorTests
{
    [Fact]
    public void CreateReturnsWorkingMonitorForThisOs()
    {
        using var monitor = SystemPressureMonitor.Create();
        var first = monitor.Sample();
        var second = monitor.Sample();
        foreach (var s in new[] { first, second })
        {
            Assert.InRange(s.CpuPercent, 0, 100);
            if (s.DiskBusyPercent is { } busy)
                Assert.InRange(busy, 0, 100);
            if (s.DiskBytesPerSecond is { } bps)
                Assert.True(bps >= 0);
        }
        if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxPressureMonitor>(monitor);
    }

    [Fact]
    public void LoadAverageFallbackNeverThrows()
    {
        using var monitor = new LoadAverageMonitor();
        var sample = monitor.Sample();
        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.Null(sample.DiskBusyPercent);
        Assert.False(sample.OnBattery);
    }

    [Fact]
    public void OwnUsageProbeReportsCpuTime()
    {
        var probe = new ProcessUsageProbe();
        var a = probe.Read();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var x = 0.0;
        while (sw.ElapsedMilliseconds < 50)
            x += Math.Sqrt(x + 1);
        var b = probe.Read();
        Assert.True(b.CpuTime >= a.CpuTime);
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/io"))
            Assert.NotNull(b.IoBytes);
        Assert.True(x > 0);
    }

    [Fact]
    public async Task BackgroundPriorityLowersNiceOnLinuxAndNeverThrows()
    {
        int? niceInside = null;
        var thread = new Thread(() =>
        {
            using (BackgroundPriority.EnterForCurrentThread())
            {
                if (OperatingSystem.IsLinux())
                    niceInside = ReadThreadNice();
            }
        });
        thread.Start();
        await Task.Run(() => thread.Join(TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux() && niceInside is not null)
            Assert.Equal(19, niceInside);
    }

    [Fact]
    public void DisposeOnAnotherThreadIsHarmless()
    {
        IDisposable? scope = null;
        var thread = new Thread(() => scope = BackgroundPriority.EnterForCurrentThread());
        thread.Start();
        thread.Join();
        scope!.Dispose();
        scope.Dispose();
    }

    /// <summary>Field 19 of /proc/thread-self/stat is the nice value.</summary>
    private static int? ReadThreadNice()
    {
        try
        {
            var text = File.ReadAllText("/proc/thread-self/stat");
            var afterComm = text[(text.LastIndexOf(')') + 2)..].Split(' ');
            // afterComm[0] is field 3 (state); nice is field 19 → index 16
            return int.Parse(afterComm[16], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
