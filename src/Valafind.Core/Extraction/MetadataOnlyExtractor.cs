using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Handles files whose content is not (or cannot be) extracted — images, audio, video, archives and legacy
/// or proprietary document formats (.doc, .xls, .ppt, .msg, Apple iWork) — by returning empty text, so the
/// file is still indexed and findable by its name and path. Never touches the file contents.
/// </summary>
public sealed class MetadataOnlyExtractor : ITextExtractor
{
    private static readonly string[] UnparsedDocumentExtensions =
        [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot", ".msg", ".pages", ".key", ".numbers", ".epub"];

    /// <inheritdoc />
    public bool CanHandle(string path) =>
        FileKinds.Classify(path) is FileKind.Image or FileKind.Audio or FileKind.Video or FileKind.Archive
        || ExtractionIO.HasExtension(path, UnparsedDocumentExtensions);

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ExtractedText?>(new ExtractedText(string.Empty, KindOf(path)));
    }

    internal static FileKind KindOf(string path)
    {
        var kind = FileKinds.Classify(path);
        if (kind != FileKind.Other)
        {
            return kind;
        }

        if (ExtractionIO.HasExtension(path, ".dot", ".docm", ".dotx", ".ott"))
        {
            return FileKind.Document;
        }

        if (ExtractionIO.HasExtension(path, ".xlt", ".xlsm", ".xltx", ".ots"))
        {
            return FileKind.Spreadsheet;
        }

        if (ExtractionIO.HasExtension(path, ".pps", ".pot", ".pptm", ".potx", ".ppsx", ".otp"))
        {
            return FileKind.Presentation;
        }

        return kind;
    }
}
