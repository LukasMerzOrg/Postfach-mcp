using System.Text;
using MimeKit;
using UglyToad.PdfPig;

namespace Postfach.Mime;

public sealed record AttachmentExtraction(string? Text, bool Truncated, string? Reason);

/// <summary>Text extraction for the attachment types Claude can use safely; everything else returns metadata only.</summary>
public static class AttachmentText
{
    public const long MaxExtractBytes = 10 * 1024 * 1024;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // executables and scripts
        ".exe", ".dll", ".com", ".bat", ".cmd", ".scr", ".msi", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf",
        ".sh", ".jar", ".app", ".apk", ".lnk", ".hta", ".cpl", ".iso", ".img",
        // archives
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab",
        // Office documents with macros
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".sldm",
    };

    public static bool IsBlocked(string? filename, string mimeType)
    {
        var ext = Path.GetExtension(filename ?? string.Empty);
        if (BlockedExtensions.Contains(ext))
        {
            return true;
        }

        return mimeType is "application/x-msdownload" or "application/x-executable" or "application/zip"
            or "application/x-zip-compressed" or "application/x-rar-compressed" or "application/x-7z-compressed"
            or "application/vnd.ms-word.document.macroenabled.12" or "application/vnd.ms-excel.sheet.macroenabled.12";
    }

    public static AttachmentExtraction Extract(MimeEntity entity, long size, int maxChars)
    {
        if (entity is not MimePart part)
        {
            if (entity is MessagePart messagePart && messagePart.Message is { } inner)
            {
                var body = BodyExtractor.Extract(inner);
                var header = $"Subject: {inner.Subject}\nFrom: {inner.From}\nDate: {inner.Date:O}\n\n";
                var full = header + body.Text;
                var text = TextTrimmer.Truncate(full, maxChars, out var t);
                return new AttachmentExtraction(text, t, null);
            }

            return new AttachmentExtraction(null, false, "unsupported_type");
        }

        var mime = part.ContentType.MimeType.ToLowerInvariant();
        var filename = part.FileName;
        if (IsBlocked(filename, mime))
        {
            return new AttachmentExtraction(null, false, "blocked_type");
        }

        if (size > MaxExtractBytes)
        {
            return new AttachmentExtraction(null, false, "too_large");
        }

        var ext = Path.GetExtension(filename ?? string.Empty).ToLowerInvariant();
        string? raw = null;
        if (mime is "text/plain" or "text/csv" or "text/calendar" or "text/markdown" or "text/tab-separated-values"
            || ext is ".txt" or ".csv" or ".ics" or ".md" or ".tsv")
        {
            raw = part is TextPart textPart ? textPart.Text : DecodeText(part);
        }
        else if (mime == "application/pdf" || ext == ".pdf")
        {
            raw = ExtractPdf(part);
            if (raw is null)
            {
                return new AttachmentExtraction(null, false, "no_text_layer");
            }
        }
        else if (mime == "text/html" || ext is ".html" or ".htm")
        {
            raw = HtmlToText.Convert(part is TextPart html ? html.Text : DecodeText(part)).Text;
        }

        if (raw is null)
        {
            return new AttachmentExtraction(null, false, "unsupported_type");
        }

        var truncatedText = TextTrimmer.Truncate(raw.Replace("\r\n", "\n"), maxChars, out var truncated);
        return new AttachmentExtraction(truncatedText, truncated, null);
    }

    private static string DecodeText(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content?.DecodeTo(stream);
        var bytes = stream.ToArray();
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static string? ExtractPdf(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content?.DecodeTo(stream);
        try
        {
            using var document = PdfDocument.Open(stream.ToArray());
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
                if (sb.Length > 200_000)
                {
                    break;
                }
            }

            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
