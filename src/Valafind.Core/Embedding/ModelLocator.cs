using Valafind.Core.Configuration;

namespace Valafind.Core.Embedding;

/// <summary>
/// Finds an installed EmbeddingGemma model directory. Purely local: it only probes the file system.
/// </summary>
public static class ModelLocator
{
    /// <summary>Environment variable that points at a model directory (overrides the default locations).</summary>
    public const string EnvironmentVariable = "VALAFIND_MODEL_DIR";

    /// <summary>Name of the model's sub-directory below a "models" folder.</summary>
    public const string ModelFolderName = "embeddinggemma-300m";

    /// <summary>ONNX variants in order of preference, relative to the model directory. fp16 variants are deliberately
    /// absent: EmbeddingGemma's activations do not support fp16.</summary>
    internal static readonly string[] PreferredModelFiles =
    [
        "onnx/model_quantized.onnx",
        "onnx/model.onnx",
        "onnx/model_q4.onnx",
        "model_quantized.onnx",
        "model.onnx",
        "model_q4.onnx",
    ];

    /// <summary>ONNX files below this size keep their weights in a sibling <c>.onnx_data</c> file.</summary>
    private const long MinSelfContainedModelBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Returns the first valid model directory among, in order: <paramref name="configuredDirectory"/>,
    /// <c>%VALAFIND_MODEL_DIR%</c>, <c>AppPaths.ModelsDirectory/embeddinggemma-300m</c>,
    /// <c>&lt;app&gt;/models/embeddinggemma-300m</c> and, on macOS,
    /// <c>&lt;bundle&gt;/Contents/Resources/models/embeddinggemma-300m</c>; or null when none is valid.
    /// </summary>
    public static string? Find(string? configuredDirectory)
    {
        foreach (var candidate in Candidates(configuredDirectory))
        {
            if (IsValid(candidate, out _))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="directory"/> holds a tokenizer (<c>tokenizer.json</c> or <c>tokenizer.model</c>) and a
    /// usable ONNX model. <paramref name="modelFile"/> receives the preferred model path relative to the directory
    /// (forward slashes, suitable for <see cref="EmbeddingOptions.ModelFile"/>), or an empty string.
    /// </summary>
    public static bool IsValid(string directory, out string modelFile)
    {
        modelFile = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return false;
            if (!File.Exists(Path.Combine(directory, "tokenizer.json")) && !File.Exists(Path.Combine(directory, "tokenizer.model")))
                return false;

            foreach (var relative in PreferredModelFiles)
            {
                if (IsUsableModelFile(directory, relative))
                {
                    modelFile = relative;
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable or malformed path: treat as "not here".
        }
        return false;
    }

    /// <summary>True when the ONNX file exists and, if its weights are external, the <c>.onnx_data</c> file too.</summary>
    internal static bool IsUsableModelFile(string directory, string relativeModelFile)
    {
        if (IsFp16(relativeModelFile))
            return false;
        var path = Path.Combine(directory, relativeModelFile.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return false;
        return info.Length >= MinSelfContainedModelBytes || File.Exists(path + "_data");
    }

    internal static bool IsFp16(string modelFile)
    {
        var name = Path.GetFileNameWithoutExtension(modelFile);
        return name.Contains("fp16", StringComparison.OrdinalIgnoreCase)
               || name.Contains("f16", StringComparison.OrdinalIgnoreCase)
               || name.Contains("half", StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<string> Candidates(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
            yield return ExpandHome(configuredDirectory);

        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            yield return ExpandHome(env);

        yield return Path.Combine(AppPaths.ModelsDirectory, ModelFolderName);

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "models", ModelFolderName);

        if (OperatingSystem.IsMacOS())
        {
            // <bundle>.app/Contents/MacOS/<exe>  ->  <bundle>.app/Contents/Resources/models/...
            var contents = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDir));
            if (contents is not null)
                yield return Path.Combine(contents, "Resources", "models", ModelFolderName);
        }
    }

    private static string ExpandHome(string path)
    {
        path = path.Trim();
        if (path == "~")
            return AppPaths.Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(AppPaths.Home, path[2..]);
        return path;
    }
}
