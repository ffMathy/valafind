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
/// <para>Every extraction runs under a wall-clock budget (<see cref="PerFileTimeout"/>, default
/// <see cref="DefaultPerFileTimeout"/>) passed to the extractor as a linked cancellation token; extractors check it
/// in their loops. A file that runs out of time, or that trips a hard resource limit (decompression bomb, absurd
/// entry counts), yields an empty-text result: it stays findable by name and is not retried endlessly.</para>
/// <para>Most extractors are CPU-bound and complete synchronously; call from a background thread.</para>
/// </remarks>
public sealed partial class DocumentReader : IDocumentReader
{
    /// <summary>Default wall-clock budget for extracting one file.</summary>
    public static readonly TimeSpan DefaultPerFileTimeout = TimeSpan.FromSeconds(20);

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
    /// <param name="perFileTimeout">Wall-clock budget per file; defaults to <see cref="DefaultPerFileTimeout"/>.</param>
    public DocumentReader(
        IEnumerable<ITextExtractor> extractors,
        ExtractionLimits? limits = null,
        ILogger? logger = null,
        TimeSpan? perFileTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(extractors);
        _extractors = extractors.ToArray();
        Limits = limits ?? new ExtractionLimits();
        _logger = logger ?? NullLogger.Instance;
        PerFileTimeout = perFileTimeout ?? DefaultPerFileTimeout;
        if (PerFileTimeout <= TimeSpan.Zero && PerFileTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(perFileTimeout), "Timeout must be positive or infinite.");
        }
    }

    /// <summary>The limits applied to every extraction.</summary>
    public ExtractionLimits Limits { get; }

    /// <summary>The wall-clock budget for extracting one file.</summary>
    public TimeSpan PerFileTimeout { get; }

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

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(PerFileTimeout);
            try
            {
                return await extractor.ExtractAsync(path, Limits, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                LogExtractionTimedOut(_logger, path, PerFileTimeout);
                return new ExtractedText(string.Empty, MetadataOnlyExtractor.KindOf(path));
            }
            catch (ExtractionLimitException ex)
            {
                LogExtractionLimited(_logger, path, ex.Message);
                return new ExtractedText(string.Empty, MetadataOnlyExtractor.KindOf(path));
            }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Text extraction of {Path} exceeded {Timeout}; indexing it by name only until the file changes")]
    private static partial void LogExtractionTimedOut(ILogger logger, string path, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Text extraction of {Path} hit a resource limit ({Reason}); indexing it by name only")]
    private static partial void LogExtractionLimited(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Text extraction failed for {Path}")]
    private static partial void LogExtractionFailed(ILogger logger, Exception exception, string path);
}
