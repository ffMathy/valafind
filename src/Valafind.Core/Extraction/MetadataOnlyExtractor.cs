using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Handles files whose content is not (or cannot be) extracted — images, audio, video, archives and legacy
/// or proprietary document formats — by returning empty text, so the file is still indexed and findable by its
/// name and path. Never touches the file contents.
/// </summary>
/// <remarks>
/// <para><b>Legacy Office and Outlook files are name-only by design.</b> Pre-2007 binary Office documents
/// (<c>.doc</c>, <c>.dot</c>, <c>.xls</c>, <c>.xlt</c>, <c>.ppt</c>, <c>.pps</c>, <c>.pot</c>) and Outlook
/// <c>.msg</c> messages are OLE2 compound files (CFB). Valafind has no CFB reader, so their text is never
/// extracted or embedded. They are searchable only through their file name and folder path. A file named
/// "contract_apartment.doc" is found by meaning, but "Doc1.doc" or "scan_0003.xls" is not found by its content.
/// Extracting their text would need a CFB container reader plus heuristics for the WordDocument, PowerPoint
/// Document and <c>__substg1.0_*</c> streams. That is a separate feature, not a missing piece of this extractor.
/// Users can still make such files searchable by saving them as .docx/.xlsx/.pptx/.eml.</para>
/// <para>Apple iWork packages (<c>.pages</c>, <c>.key</c>, <c>.numbers</c>) are name-only for the same reason
/// (proprietary IWA protobuf streams). <c>.epub</c> is listed only as a fallback: the default
/// <see cref="DocumentReader"/> registers <see cref="EpubExtractor"/> first, so e-books are read in full.</para>
/// </remarks>
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
