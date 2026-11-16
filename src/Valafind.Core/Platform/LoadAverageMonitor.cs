using System.Runtime.InteropServices;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>Portable fallback: whole-machine CPU estimated from the 1-minute load average
/// (getloadavg) divided by the processor count. Disk metrics are unknown; battery is assumed false.</summary>
internal sealed partial class LoadAverageMonitor : ISystemPressureMonitor
{
    private bool _unavailable = OperatingSystem.IsWindows();

    public PressureSample Sample() => new(DateTimeOffset.UtcNow, ReadCpuPercent(), null, null, false);

    private unsafe double ReadCpuPercent()
    {
        if (_unavailable)
            return 0;
        try
        {
            var load = stackalloc double[1];
            if (getloadavg(load, 1) < 1)
                return 0;
            return LoadToPercent(load[0], Environment.ProcessorCount);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return 0;
        }
    }

    internal static double LoadToPercent(double load, int processorCount) =>
        double.IsFinite(load) ? Math.Clamp(load / Math.Max(1, processorCount) * 100.0, 0, 100) : 0;

    public void Dispose()
    {
    }

    [LibraryImport("libc")]
    private static unsafe partial int getloadavg(double* loadavg, int count);
}
