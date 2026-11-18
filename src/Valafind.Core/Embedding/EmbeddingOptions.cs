namespace Valafind.Core.Embedding;

/// <summary>Where <see cref="EmbeddingGemmaModel"/> runs inference.</summary>
public enum EmbeddingDevice
{
    /// <summary>The GPU when one is usable (see <see cref="EmbeddingGemmaModel.ActiveDevice"/>), otherwise the CPU.</summary>
    Auto = 0,

    /// <summary>Always the CPU.</summary>
    Cpu = 1,

    /// <summary>The GPU; loading fails when none is usable.</summary>
    Gpu = 2,
}

/// <summary>Configuration for <see cref="EmbeddingGemmaModel"/>.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Largest token count (prompt and special tokens included) EmbeddingGemma 2 accepts.</summary>
    public const int HardMaxTokens = 8192;

    /// <summary>Directory holding the tokenizer (<c>tokenizer.json</c> and/or <c>tokenizer.model</c>) and the ONNX
    /// model. Usually the result of <see cref="ModelLocator.Find"/>.</summary>
    public required string ModelDirectory { get; set; }

    /// <summary>ONNX model path relative to <see cref="ModelDirectory"/>. fp16 variants are rejected because
    /// EmbeddingGemma's activations overflow in fp16. When left at the default and that file is missing, the best
    /// variant found by <see cref="ModelLocator.IsValid"/> is used instead.</summary>
    public string ModelFile { get; set; } = DefaultModelFile;

    /// <summary>Matryoshka output size: 768, 512, 256 or 128.</summary>
    public int Dimensions { get; set; } = 768;

    /// <summary>Token budget per input including the task prompt and BOS/EOS (hard cap <see cref="HardMaxTokens"/>).
    /// Longer inputs are truncated.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>Where inference runs. <see cref="EmbeddingDevice.Auto"/>: the GPU when one is usable, else the CPU.</summary>
    public EmbeddingDevice Device { get; set; } = EmbeddingDevice.Cpu;

    /// <summary>File that exists only while a GPU session runs its first inference (null: none). Some GPU kernels abort
    /// the process instead of failing; when a start finds this file left behind by the same build, <see cref="EmbeddingDevice.Auto"/>
    /// stays on the CPU instead of crashing again.</summary>
    public string? GpuCrashMarker { get; set; }

    /// <summary>ONNX Runtime intra-op threads. Keep low: indexing runs in the background.</summary>
    public int IntraOpThreads { get; set; } = 2;

    /// <summary>Documents per inference call in <see cref="EmbeddingGemmaModel.EmbedDocuments"/>.</summary>
    public int BatchSize { get; set; } = 8;

    /// <summary>Use the vision and audio encoders when they are installed. False: text only.</summary>
    public bool EnableMedia { get; set; } = true;

    /// <summary>Vision soft tokens per picture: 70, 140, 280 (the model's default), 560 or 1120. More tokens see more
    /// detail but cost proportionally more CPU (≈ 1.4 s, 2.8 s and 6.4 s per picture on two cores for 70/140/280). 140
    /// keeps indexing a photo library practical in the background.</summary>
    public int ImageSoftTokens { get; set; } = 140;

    /// <summary>Vision soft tokens per video frame (the model's default is 140).</summary>
    public int VideoFrameSoftTokens { get; set; } = 70;

    /// <summary>Frames per video (sampled at one per second, then thinned out evenly; the model's default is 32).</summary>
    public int MaxVideoFrames { get; set; } = 8;
    internal const string DefaultModelFile = "onnx/model_quantized.onnx";

    internal static readonly int[] SupportedDimensions = [768, 512, 256, 128];

    /// <summary>Throws <see cref="ArgumentException"/> when a value is out of range.</summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelDirectory))
            throw new ArgumentException("ModelDirectory must be set.", nameof(ModelDirectory));
        if (string.IsNullOrWhiteSpace(ModelFile))
            throw new ArgumentException("ModelFile must be set.", nameof(ModelFile));
        if (Array.IndexOf(SupportedDimensions, Dimensions) < 0)
            throw new ArgumentOutOfRangeException(nameof(Dimensions), Dimensions, "Dimensions must be 768, 512, 256 or 128.");
        if (MaxTokens is < EmbeddingGemmaModel.MinTokens or > HardMaxTokens)
            throw new ArgumentOutOfRangeException(nameof(MaxTokens), MaxTokens,
                $"MaxTokens must be between {EmbeddingGemmaModel.MinTokens} and {HardMaxTokens}.");
        if (IntraOpThreads < 0)
            throw new ArgumentOutOfRangeException(nameof(IntraOpThreads), IntraOpThreads, "IntraOpThreads must be >= 0 (0 = runtime default).");
        if (BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, "BatchSize must be >= 1.");
        if (Array.IndexOf(Media.VisionPreprocessor.SupportedSoftTokens, ImageSoftTokens) < 0)
            throw new ArgumentOutOfRangeException(nameof(ImageSoftTokens), ImageSoftTokens, "ImageSoftTokens must be 70, 140, 280, 560 or 1120.");
        if (Array.IndexOf(Media.VisionPreprocessor.SupportedSoftTokens, VideoFrameSoftTokens) < 0)
            throw new ArgumentOutOfRangeException(nameof(VideoFrameSoftTokens), VideoFrameSoftTokens, "VideoFrameSoftTokens must be 70, 140, 280, 560 or 1120.");
        if (MaxVideoFrames is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaxVideoFrames), MaxVideoFrames, "MaxVideoFrames must be between 1 and 32.");
    }
}
