using System.Globalization;
using MimeKit;
using Valafind.Core.Abstractions;

namespace Valafind.Core.Extraction;

/// <summary>
/// Extracts e-mail text with MimeKit: a short header block (Subject, From, To, Date) followed by the body
/// (text/plain preferred, otherwise HTML converted to text). Handles single messages (.eml) and Unix
/// mailboxes (.mbox, up to <see cref="MaxMboxMessages"/> messages or the character budget). Attachments are
/// not extracted. Outlook .msg files are not handled here (see <see cref="MetadataOnlyExtractor"/>).
/// </summary>
public sealed class MailExtractor : ITextExtractor
{
    /// <summary>Upper bound on messages read from one mailbox file.</summary>
    internal const int MaxMboxMessages = 500;

    /// <inheritdoc />
    public bool CanHandle(string path) => ExtractionIO.HasExtension(path, ".eml", ".mbox");

    /// <inheritdoc />
    public Task<ExtractedText?> ExtractAsync(string path, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var sink = new TextAccumulator(limits.MaxChars);
        try
        {
            using var stream = ExtractionIO.OpenRead(path);
            if (ExtractionIO.HasExtension(path, ".mbox"))
            {
                // Source storage keeps part content in the (seekable) file instead of copying it to memory —
                // the MimeKit 5 equivalent of the old "persistent" parser mode.
                var parser = new MimeParser(stream, MimeFormat.Mbox, MimeContentStorage.Source);
                var count = 0;
                while (!parser.IsEndOfStream && count < MaxMboxMessages && !sink.IsFull)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var message = parser.ParseMessage(cancellationToken);
                    sink.EnsureBlankLine();
                    AppendMessage(message, sink);
                    count++;
                }

                if (count == 0)
                {
                    return Task.FromResult<ExtractedText?>(null);
                }
            }
            else
            {
                using var message = MimeMessage.Load(stream, cancellationToken);
                AppendMessage(message, sink);
            }
        }
        catch (FormatException)
        {
            // MimeKit reports unparseable input with FormatException (e.g. no mbox "From " marker).
            if (sink.Length == 0)
            {
                return Task.FromResult<ExtractedText?>(null);
            }
        }

        return Task.FromResult<ExtractedText?>(new ExtractedText(sink.ToString(), FileKind.Mail));
    }

    internal static void AppendMessage(MimeMessage message, TextAccumulator sink)
    {
        AppendHeader(sink, "Subject", message.Subject);
        AppendHeader(sink, "From", FormatAddresses(message.From));
        AppendHeader(sink, "To", FormatAddresses(message.To));
        if (message.Headers.Contains(HeaderId.Date))
        {
            AppendHeader(sink, "Date", message.Date.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        }

        sink.Append('\n');

        var body = message.TextBody;
        if (!string.IsNullOrWhiteSpace(body))
        {
            sink.Append(body.Trim());
        }
        else if (message.HtmlBody is { Length: > 0 } html)
        {
            HtmlText.AppendText(html, sink);
        }
    }

    private static void AppendHeader(TextAccumulator sink, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sink.Append(name);
        sink.Append(": ");
        sink.Append(value.Trim());
        sink.Append('\n');
    }

    private static string FormatAddresses(InternetAddressList list)
    {
        var parts = new List<string>(list.Count);
        foreach (var address in list)
        {
            parts.Add(address switch
            {
                MailboxAddress { Name.Length: > 0 } m => $"{m.Name} <{m.Address}>",
                MailboxAddress m => m.Address,
                GroupAddress g => g.Name ?? string.Empty,
                _ => address.ToString(),
            });
        }

        return string.Join(", ", parts);
    }
}
