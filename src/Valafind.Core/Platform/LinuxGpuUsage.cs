using System.Runtime.InteropServices;

namespace Valafind.Core.Platform;

/// <summary>
/// Utilisation of the busiest GPU on Linux: the kernel's <c>gpu_busy_percent</c> in sysfs (amdgpu, and Intel's xe
/// driver) and NVIDIA's NVML (<c>libnvidia-ml.so.1</c>, shipped with the proprietary driver, loaded on demand). Null when
/// neither is available. Never throws; on other systems it simply finds nothing.
/// </summary>
internal sealed class LinuxGpuUsage : IDisposable
{
    private const string DrmPath = "/sys/class/drm";

    private readonly string[] _busyFiles;
    private Nvml? _nvml;
    private bool _nvmlTried;

    public LinuxGpuUsage(string drmPath = DrmPath)
    {
        _busyFiles = FindBusyFiles(drmPath);
    }

    /// <summary>Busiest GPU's utilisation in percent, or null when no GPU reports one.</summary>
    public double? Read()
    {
        double? max = null;
        foreach (var file in _busyFiles)
        {
            if (TryParsePercent(SafeFile.TryReadAllText(file)) is { } value)
                max = Math.Max(max ?? 0, value);
        }
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            _nvml = Nvml.TryOpen();
        }
        if (_nvml?.MaxUtilisation() is { } nvidia)
            max = Math.Max(max ?? 0, nvidia);
        return max;
    }

    /// <summary>Parses a sysfs percentage ("37\n"); null when it is not one.</summary>
    internal static double? TryParsePercent(string? text) =>
        int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100)
            : null;

    /// <summary>One <c>gpu_busy_percent</c> per card ("card0", not the "card0-DP-1" connectors).</summary>
    internal static string[] FindBusyFiles(string drmPath)
    {
        try
        {
            if (!Directory.Exists(drmPath))
                return [];
            return [.. Directory.EnumerateDirectories(drmPath, "card*")
                .Where(d => !Path.GetFileName(d).Contains('-', StringComparison.Ordinal))
                .Select(d => Path.Combine(d, "device", "gpu_busy_percent"))
                .Where(File.Exists)
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _nvml?.Dispose();
        _nvml = null;
    }

    /// <summary>The few NVML entry points needed, bound with <see cref="NativeLibrary"/> so machines without an NVIDIA
    /// driver pay nothing.</summary>
    private sealed unsafe class Nvml : IDisposable
    {
        private const int Success = 0;

        private readonly nint _library;
        private readonly delegate* unmanaged<int> _shutdown;
        private readonly delegate* unmanaged<uint*, int> _getCount;
        private readonly delegate* unmanaged<uint, nint*, int> _getHandle;
        private readonly delegate* unmanaged<nint, uint*, int> _getUtilisation;
        private bool _failed;

        private Nvml(nint library)
        {
            _library = library;
            _shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlShutdown");
            _getCount = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetCount_v2");
            _getHandle = (delegate* unmanaged<uint, nint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByIndex_v2");
            _getUtilisation = (delegate* unmanaged<nint, uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetUtilizationRates");
        }

        public static Nvml? TryOpen()
        {
            if (!NativeLibrary.TryLoad("libnvidia-ml.so.1", out var library))
                return null;
            try
            {
                var init = (delegate* unmanaged<int>)NativeLibrary.GetExport(library, "nvmlInit_v2");
                var nvml = new Nvml(library);
                if (init() == Success)
                    return nvml;
            }
            catch (EntryPointNotFoundException)
            {
            }
            NativeLibrary.Free(library);
            return null;
        }

        public double? MaxUtilisation()
        {
            if (_failed)
                return null;
            uint count;
            if (_getCount(&count) != Success)
            {
                _failed = true;
                return null;
            }
            double? max = null;
            var rates = stackalloc uint[2]; // nvmlUtilization_t { gpu, memory }
            for (uint i = 0; i < count; i++)
            {
                nint device;
                if (_getHandle(i, &device) == Success && _getUtilisation(device, rates) == Success)
                    max = Math.Max(max ?? 0, Math.Clamp(rates[0], 0u, 100u));
            }
            return max;
        }

        public void Dispose()
        {
            _ = _shutdown();
            NativeLibrary.Free(_library);
        }
    }
}
