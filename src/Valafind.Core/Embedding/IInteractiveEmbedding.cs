using Valafind.Core.Abstractions;

namespace Valafind.Core.Embedding;

/// <summary>
/// Optional capability of an <see cref="IEmbeddingModel"/>: embed a few documents with the priority of a query, for
/// user-facing work (search highlighting) that must not wait behind background indexing batches.
/// </summary>
internal interface IInteractiveEmbedding
{
    /// <summary>Same output as <see cref="IEmbeddingModel.EmbedDocuments"/>, but scheduled like
    /// <see cref="IEmbeddingModel.EmbedQuery"/>: it preempts a running background batch and runs before waiting ones.
    /// Meant for small inputs only.</summary>
    IReadOnlyList<float[]> EmbedDocumentsInteractive(IReadOnlyList<DocumentText> documents, CancellationToken cancellationToken = default);
}
