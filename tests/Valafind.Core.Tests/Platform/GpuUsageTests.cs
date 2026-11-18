#pragma warning disable CA1707 // xunit test names use underscores for readability

using Valafind.Core.Platform;
using Xunit;

namespace Valafind.Core.Tests.Platform;

public class GpuUsageTests
{
    private const string Luid = "luid_0x00000000_0x0000D1B5_phys_0";

    [Fact]
    public void Windows_engines_sum_over_processes_and_the_busiest_engine_wins()
    {
        var (total, own) = GpuEngineMath.Aggregate(
        [
            ($"pid_100_{Luid}_eng_0_engtype_3D", 30),
            ($"pid_200_{Luid}_eng_0_engtype_3D", 25),
            ($"pid_42_{Luid}_eng_1_engtype_Compute", 40),
            ($"pid_42_{Luid}_eng_0_engtype_3D", 10),
            ("_Total", 99),
            ("pid_x_garbage", 50),
        ], ownProcessId: 42);
        Assert.Equal(65, total);
        Assert.Equal(10, own);
    }

    [Fact]
    public void Windows_without_instances_reports_nothing()
    {
        Assert.Equal((null, null), GpuEngineMath.Aggregate([], 1));
    }

    [Theory]
    [InlineData("37\n", 37.0)]
    [InlineData("150", 100.0)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Linux_sysfs_percent_is_parsed(string? text, double? expected)
    {
        Assert.Equal(expected, LinuxGpuUsage.TryParsePercent(text));
    }

    [Fact]
    public void Linux_reads_the_busiest_card_and_skips_connectors()
    {
        var root = Directory.CreateTempSubdirectory("vf-drm-").FullName;
        try
        {
            void Card(string name, string? busy)
            {
                var device = Directory.CreateDirectory(Path.Combine(root, name, "device")).FullName;
                if (busy is not null)
                    File.WriteAllText(Path.Combine(device, "gpu_busy_percent"), busy);
            }
            Card("card0", "12\n");
            Card("card1", "64\n");
            Card("card1-DP-1", "99\n");
            Card("card2", null);

            Assert.Equal(2, LinuxGpuUsage.FindBusyFiles(root).Length);
            if (OperatingSystem.IsLinux())
            {
                using var gpu = new LinuxGpuUsage(root);
                Assert.True(gpu.Read() >= 64); // an NVIDIA GPU on the test machine may report more
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
