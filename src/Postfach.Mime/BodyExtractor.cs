using MimeKit;

namespace Postfach.Mime;

public enum BodyFormat
{
    /// <summary>text/plain when present, otherwise sanitized HTML.</summary>
    Text,

    /// <summary>Always derive the text from the HTML part when there is one.</summary>
    HtmlStripped,

    /// <summary>Only the raw header block.</summary>
    RawHeaders,
}

public sealed record ExtractedBody(string Text, string Source, int HiddenTextRemoved);

public static class BodyExtractor
{
    public static ExtractedBody Extract(MimeMessage message, BodyFormat format = BodyFormat.Text)
    {
        if (format == BodyFormat.RawHeaders)
        {
            return new ExtractedBody(RawHeaders(message), "headers", 0);
        }

        var result = FromParts(message.GetTextBody(MimeKit.Text.TextFormat.Plain), message.HtmlBody, format);
        if (result.Source != "none")
        {
            return result;
        }

        // Fall back to the first textual part of any other subtype (e.g. text/enriched).
        var other = message.BodyParts.OfType<TextPart>().FirstOrDefault(p => !p.IsAttachment);
        return other is null
            ? result
            : new ExtractedBody(CleanPlain(other.Text), other.ContentType.MimeType, 0);
    }

    /// <summary>Builds the clean body from already decoded text/plain and text/html alternatives.</summary>
    public static ExtractedBody FromParts(string? plain, string? html, BodyFormat format = BodyFormat.Text)
    {
        if (format == BodyFormat.Text && !string.IsNullOrWhiteSpace(plain))
        {
            return new ExtractedBody(CleanPlain(plain), "text/plain", 0);
        }

        if (!string.IsNullOrWhiteSpace(html))
        {
            var converted = HtmlToText.Convert(html);
            return new ExtractedBody(TextTrimmer.CollapseQuotes(converted.Text), "text/html", converted.HiddenTextRemoved);
        }

        if (!string.IsNullOrWhiteSpace(plain))
        {
            return new ExtractedBody(CleanPlain(plain), "text/plain", 0);
        }

        return new ExtractedBody(string.Empty, "none", 0);
    }

    public static string CleanPlain(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = string.Join('\n', normalized.Split('\n').Select(l => l.TrimEnd()));
        return TextTrimmer.CollapseQuotes(normalized.Trim());
    }

    public static string RawHeaders(MimeMessage message)
    {
        using var writer = new StringWriter();
        foreach (var header in message.Headers)
        {
            writer.Write(header.Field);
            writer.Write(": ");
            writer.Write(header.Value);
            writer.Write('\n');
        }

        return writer.ToString().TrimEnd();
    }
}
