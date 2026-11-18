using System.Globalization;

namespace Valafind.Core.Platform;

/// <summary>Turns Windows' per-process, per-engine "GPU Engine" utilisation into one number per machine, the way Task
/// Manager does: an engine's utilisation is the sum over the processes using it, and a GPU is as busy as its busiest
/// engine.</summary>
internal static class GpuEngineMath
{
    /// <summary>Busiest engine across all GPUs, and Valafind's own utilisation on that same engine.</summary>
    /// <param name="instances">Counter instances such as
    /// "pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_Compute" with their utilisation in percent.</param>
    /// <param name="ownProcessId">This process.</param>
    /// <returns>(null, null) when no instance could be parsed.</returns>
    public static (double? Total, double? Own) Aggregate(IEnumerable<(string Name, double Percent)> instances, int ownProcessId)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var own = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, percent) in instances)
        {
            if (!TryParse(name, out var pid, out var engine) || !double.IsFinite(percent))
                continue;
            var value = Math.Max(0, percent);
            totals[engine] = totals.GetValueOrDefault(engine) + value;
            if (pid == ownProcessId)
                own[engine] = own.GetValueOrDefault(engine) + value;
        }
        if (totals.Count == 0)
            return (null, null);
        var busiest = totals.MaxBy(kv => kv.Value);
        return (Math.Clamp(busiest.Value, 0, 100), Math.Clamp(own.GetValueOrDefault(busiest.Key), 0, 100));
    }

    /// <summary>Splits "pid_&lt;n&gt;_&lt;engine key&gt;" into the process id and the engine key (everything after the pid,
    /// which names the adapter, physical GPU and engine).</summary>
    internal static bool TryParse(string name, out int pid, out string engine)
    {
        pid = 0;
        engine = string.Empty;
        if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            return false;
        var end = name.IndexOf('_', 4);
        if (end < 0 || !int.TryParse(name.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            return false;
        engine = name[(end + 1)..];
        return engine.Length > 0;
    }
}
