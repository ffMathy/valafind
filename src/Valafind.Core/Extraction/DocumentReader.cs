using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Picks the first <see cref="ITextExtractor"/> that can handle a path and runs it within
/// <see cref="ExtractionLimits"/>.
/// </summary>
/// <remarks>
/// <para><see cref="ReadAsync"/> never throws for missing, locked, unreadable or corrupt files: it returns
/// null. Only <see cref="OperationCanceledException"/> (for the caller's token) propagates.</para>
/// <para>Files larger than <see cref="ExtractionLimits.MaxFileBytes"/> yield an empty-text result (findable by
/// name) instead of being parsed. Files that no extractor handles (e.g. binaries with unknown extensions)
/// yield null.</para>
/// <para>Most extractors are CPU-bound and complete synchronously; call from a background thread.</para>
/// </remarks>
public sealed partial class DocumentReader : IDocumentReader
{
    private readonly ITextExtractor[] _extractors;
    private readonly ILogger _logger;

    /// <summary>Creates a reader with all built-in extractors and default <see cref="ExtractionLimits"/>.</summary>
    public DocumentReader()
        : this(CreateDefaultExtractors())
    {
    }

    /// <summary>Creates a reader with the given extractors, tried in order.</summary>
    /// <param name="extractors">Extractors in priority order; the first whose <see cref="ITextExtractor.CanHandle"/> returns true is used.</param>
    /// <param name="limits">Size limits; defaults to <c>new ExtractionLimits()</c>.</param>
    /// <param name="logger">Optional logger for (debug-level) extraction failures.</param>
    public DocumentReader(IEnumerable<ITextExtractor> extractors, ExtractionLimits? limits = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToArray();
        Limits = limits ?? new ExtractionLimits();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The limits applied to every extraction.</summary>
    public ExtractionLimits Limits { get; }

    /// <summary>The built-in extractors in their default priority order.</summary>
    public static IReadOnlyList<ITextExtractor> CreateDefaultExtractors() =>
    [
        new PdfExtractor(),
        new OpenXmlExtractor(),
        new OpenDocumentExtractor(),
        new EpubExtractor(),
        new MailExtractor(),
        new NotebookExtractor(),
        new RtfExtractor(),
        new PlainTextExtractor(),
        new MetadataOnlyExtractor(),
    ];

    /// <inheritdoc />
    public async Task<ExtractedText?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var extractor = Select(path);
            if (extractor is null)
            {
                return null;
            }

            if (info.Length > Limits.MaxFileBytes)
            {
                return new ExtractedText(string.Empty, MetadataOnlyExtractor.KindOf(path));
            }

            return await extractor.ExtractAsync(path, Limits, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Parsers of untrusted files fail in many ways (IO, format, index/argument errors, even OOM on
            // absurd length fields). None of these should stop indexing.
            LogExtractionFailed(_logger, ex, path);
            return null;
        }
    }

    private ITextExtractor? Select(string path)
    {
        foreach (var extractor in _extractors)
        {
            if (extractor.CanHandle(path))
            {
                return extractor;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Text extraction failed for {Path}")]
    private static partial void LogExtractionFailed(ILogger logger, Exception exception, string path);
}
