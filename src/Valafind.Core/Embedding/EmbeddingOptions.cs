namespace Valafind.Core.Embedding;

/// <summary>Configuration for <see cref="EmbeddingGemmaModel"/>.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>Largest token count (prompt and special tokens included) EmbeddingGemma accepts.</summary>
    public const int HardMaxTokens = 2048;

    /// <summary>Directory holding the tokenizer (<c>tokenizer.json</c> and/or <c>tokenizer.model</c>) and the ONNX
    /// model. Usually the result of <see cref="ModelLocator.Find"/>.</summary>
    public required string ModelDirectory { get; set; }

    /// <summary>ONNX model path relative to <see cref="ModelDirectory"/>. fp16 variants are rejected because
    /// EmbeddingGemma's activations overflow in fp16. When left at the default and that file is missing, the best
    /// variant found by <see cref="ModelLocator.IsValid"/> is used instead.</summary>
    public string ModelFile { get; set; } = DefaultModelFile;

    /// <summary>Matryoshka output size: 768, 512, 256 or 128.</summary>
    public int Dimensions { get; set; } = 256;

    /// <summary>Token budget per input including the task prompt and BOS/EOS (hard cap <see cref="HardMaxTokens"/>).
    /// Longer inputs are truncated.</summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>ONNX Runtime intra-op threads. Keep low: indexing runs in the background.</summary>
    public int IntraOpThreads { get; set; } = 2;

    /// <summary>Documents per inference call in <see cref="EmbeddingGemmaModel.EmbedDocuments"/>.</summary>
    public int BatchSize { get; set; } = 8;

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
    }
}
