using Valafind.Core.Configuration;

namespace Valafind.Core.Embedding;

/// <summary>
/// Finds an installed EmbeddingGemma 2 model directory. Purely local: it only probes the file system.
/// </summary>
public static class ModelLocator
{
    /// <summary>Name of the model's sub-directory below a "models" folder.</summary>
    public const string ModelFolderName = "embeddinggemma-2";

    /// <summary>Folder of the previous model (EmbeddingGemma-300m, text only), which earlier versions installed. It
    /// is never loaded: its vectors live in a different space.</summary>
    public const string LegacyModelFolderName = "embeddinggemma-300m";

    /// <summary><c>model_type</c> in <c>config.json</c> of an EmbeddingGemma 2 export.</summary>
    internal const string ModelType = "embedding_gemma2";

    /// <summary>ONNX variants of the text model in order of preference, relative to the model directory. fp16 variants
    /// are deliberately absent: Gemma activations overflow in fp16.</summary>
    internal static readonly string[] PreferredModelFiles =
    [
        "onnx/model_quantized.onnx",
        "onnx/model.onnx",
        "onnx/model_q4.onnx",
        "model_quantized.onnx",
        "model.onnx",
        "model_q4.onnx",
    ];

    /// <summary>Vision encoder variants (images and video frames) in order of preference.</summary>
    internal static readonly string[] PreferredVisionEncoderFiles =
    [
        "onnx/vision_encoder_quantized.onnx",
        "onnx/vision_encoder.onnx",
        "onnx/vision_encoder_q4.onnx",
    ];

    /// <summary>Audio encoder variants in order of preference.</summary>
    internal static readonly string[] PreferredAudioEncoderFiles =
    [
        "onnx/audio_encoder_quantized.onnx",
        "onnx/audio_encoder.onnx",
        "onnx/audio_encoder_q4.onnx",
    ];

    /// <summary>ONNX files below this size keep their weights in a sibling <c>.onnx_data</c> file.</summary>
    private const long MinSelfContainedModelBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Returns the first valid model directory among, in order: <c>AppPaths.ModelsDirectory/embeddinggemma-2</c>,
    /// <c>&lt;app&gt;/models/embeddinggemma-2</c> and, on macOS,
    /// <c>&lt;bundle&gt;/Contents/Resources/models/embeddinggemma-2</c>; or null when none is valid.
    /// </summary>
    public static string? Find()
    {
        foreach (var candidate in Candidates())
        {
            if (IsValid(candidate, out _))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="directory"/> holds an EmbeddingGemma 2 export (its <c>config.json</c> says so), a
    /// tokenizer (<c>tokenizer.json</c> or <c>tokenizer.model</c>) and a usable ONNX text model. The vision and audio
    /// encoders are optional (see <see cref="FindVisionEncoder"/>, <see cref="FindAudioEncoder"/>).
    /// <paramref name="modelFile"/> receives the preferred model path relative to the directory
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
            if (!IsEmbeddingGemma2(directory))
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

    /// <summary>The vision encoder in <paramref name="directory"/> (relative path), or null when it is not installed.</summary>
    public static string? FindVisionEncoder(string directory) => FindFirstUsable(directory, PreferredVisionEncoderFiles);

    /// <summary>The audio encoder in <paramref name="directory"/> (relative path), or null when it is not installed.</summary>
    public static string? FindAudioEncoder(string directory) => FindFirstUsable(directory, PreferredAudioEncoderFiles);

    private static string? FindFirstUsable(string directory, string[] candidates)
    {
        try
        {
            foreach (var relative in candidates)
            {
                if (IsUsableModelFile(directory, relative))
                    return relative;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    /// <summary>True when <c>config.json</c> names the EmbeddingGemma 2 architecture. A folder of the previous text-only
    /// model (or anything else) is rejected: its inputs and its vector space differ.</summary>
    internal static bool IsEmbeddingGemma2(string directory)
    {
        var path = Path.Combine(directory, "config.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 1024 * 1024)
            return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && document.RootElement.TryGetProperty("model_type", out var type)
                   && type.ValueKind == System.Text.Json.JsonValueKind.String
                   && type.GetString() == ModelType;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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

    internal static IEnumerable<string> Candidates()
    {
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
}
