using Valafind.Core.Abstractions;

namespace Valafind.Core.Platform;

/// <summary>
/// Factory for the operating-system specific <see cref="ISystemPressureMonitor"/>.
/// All implementations are local-only (no network I/O) and never throw from <see cref="ISystemPressureMonitor.Sample"/>;
/// metrics the OS cannot provide are reported as <c>null</c>.
/// </summary>
public static class SystemPressureMonitor
{
    /// <summary>Creates the best monitor for the current OS, falling back to a load-average based estimate.</summary>
    public static ISystemPressureMonitor Create()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists(LinuxPressureMonitor.ProcStatPath))
                return new LinuxPressureMonitor();
            if (OperatingSystem.IsWindows())
                return new WindowsPressureMonitor();
            if (OperatingSystem.IsMacOS())
                return new MacPressureMonitor();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            // Fall through to the portable estimate.
        }
        return new LoadAverageMonitor();
    }
}
