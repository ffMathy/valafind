using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace Valafind.Core.Embedding;

/// <summary>
/// ONNX Runtime's WebGPU plugin execution provider (Dawn over D3D12 on Windows, Vulkan on Linux, Metal on macOS): runs
/// the models on the GPU without vendor toolkits (no CUDA or cuDNN install). The plugin library is registered with
/// ONNX Runtime once per process; a machine without a usable adapter, driver or Vulkan loader simply has no device.
/// </summary>
internal static class WebGpuDevice
{
    /// <summary>Name under which the plugin library is registered with the ORT environment.</summary>
    private const string RegistrationName = "valafind_webgpu";

    // Provider option keys as the plugin expects them (ORT prefixes "ep.webgpuexecutionprovider." itself).
    private const string PowerPreferenceKey = "powerPreference";
    private const string ForceCpuNodeNamesKey = "forceCpuNodeNames";

    /// <summary>
    /// Nodes kept on the CPU because running them with WebGPU (plugin 0.5.0) aborts the whole process (a native
    /// fail-fast, not an error that could be caught and fallen back from). Found by bisecting with
    /// <c>forceCpuNodeNames</c> on an NVIDIA GTX 1070 (D3D12 and Vulkan alike):
    /// <list type="bullet">
    /// <item>every <c>GatherBlockQuantized</c>: the text model's 8-bit token-embedding lookup (a cheap row lookup on the
    /// CPU);</item>
    /// <item>the audio encoder's <c>Unsqueeze</c> <c>node_unsqueeze_1</c>, which reshapes a sequence length. The abort
    /// depends on how its neighbours are placed (moving every <c>Unsqueeze</c> to the CPU aborts again), so this one
    /// is matched by name.</item>
    /// </list>
    /// A first inference with the crash marker in place (see <see cref="EmbeddingOptions.GpuCrashMarker"/>) guards
    /// against kernels that fail like this on other GPUs.
    /// </summary>
    internal static List<string> CpuNodes(byte[] graph)
    {
        var nodes = OnnxGraph.NodeNames(graph, "GatherBlockQuantized");
        nodes.AddRange(OnnxGraph.NodeNames(graph, "Unsqueeze").Where(name => name == "node_unsqueeze_1"));
        return nodes;
    }

    private static readonly Lock Gate = new();
    private static bool _attempted;
    private static OrtEpDevice? _device;
    private static string? _unavailableReason;

    /// <summary>The WebGPU device, or null (with the reason) when the GPU cannot be used on this machine.</summary>
    public static OrtEpDevice? Find(out string? unavailableReason)
    {
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                try
                {
                    var env = OrtEnv.Instance();
                    env.RegisterExecutionProviderLibrary(RegistrationName, LibraryPath());
                    var name = WebGpuEp.GetEpName();
                    // Several entries can describe one GPU (one per DXGI adapter that exposes it); the first is the
                    // high-performance one.
                    _device = env.GetEpDevices().FirstOrDefault(d => d.EpName == name);
                    if (_device is null)
                        _unavailableReason = "no WebGPU adapter found (GPU driver, or on Linux the Vulkan loader, missing)";
                }
#pragma warning disable CA1031 // any failure here only means "no GPU": the caller falls back to the CPU
                catch (Exception e)
#pragma warning restore CA1031
                {
                    _unavailableReason = $"WebGPU provider could not be loaded: {e.Message}";
                }
            }
            unavailableReason = _unavailableReason;
            return _device;
        }
    }

    /// <summary>
    /// Full path of the provider library. A single-file publish unpacks native libraries into its extraction folder
    /// (listed in <c>NATIVE_DLL_SEARCH_DIRECTORIES</c>), where the package's own probe of <c>runtimes/&lt;rid&gt;/native</c>
    /// does not look; a regular build has it next to the app or under <c>runtimes/</c>.
    /// </summary>
    private static string LibraryPath()
    {
        var name = OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll"
            : OperatingSystem.IsMacOS() ? "libonnxruntime_providers_webgpu.dylib"
            : "libonnxruntime_providers_webgpu.so";
        var directories = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(AppContext.BaseDirectory);
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return WebGpuEp.GetLibraryPath(); // runtimes/<rid>/native; throws when the platform has no build
    }

    /// <summary>Human-readable adapter description, e.g. "GPU: NVIDIA GeForce GTX 1070 (WebGPU)".</summary>
    public static string Describe(OrtEpDevice device)
    {
        var hardware = device.HardwareDevice;
        var name = hardware.Metadata.Entries.TryGetValue("Description", out var description) && !string.IsNullOrWhiteSpace(description)
            ? description
            : string.IsNullOrWhiteSpace(hardware.Vendor) ? device.EpVendor : hardware.Vendor;
        return $"GPU: {name} (WebGPU)";
    }

    /// <summary>Adds the WebGPU provider (ahead of the CPU provider, which runs whatever it does not support).</summary>
    /// <param name="options">Session options to add it to.</param>
    /// <param name="device">From <see cref="Find"/>.</param>
    /// <param name="cpuNodeNames">Nodes to keep on the CPU (see <see cref="CpuNodes"/>).</param>
    public static void Append(SessionOptions options, OrtEpDevice device, IReadOnlyCollection<string> cpuNodeNames)
    {
        var providerOptions = new Dictionary<string, string>
        {
            // A laptop with an integrated and a discrete GPU: take the discrete one.
            [PowerPreferenceKey] = "high-performance",
        };
        if (cpuNodeNames.Count > 0)
            providerOptions[ForceCpuNodeNamesKey] = string.Join('\n', cpuNodeNames);
        options.AppendExecutionProvider(OrtEnv.Instance(), [device], providerOptions);
    }
}
