using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Markdig;
using MimeKit;
using MimeKit.Utils;
using Postfach.Imap;
using Postfach.Mime;

namespace Postfach.Send;

public sealed record DraftRequest
{
    public required string Account { get; init; }

    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public string? Subject { get; init; }

    /// <summary>Markdown; rendered to text/plain (the Markdown itself) and text/html.</summary>
    public string? Body { get; init; }

    public string? InReplyTo { get; init; }

    public string? ForwardOf { get; init; }

    /// <summary>From address; must be one of the account's identities.</summary>
    public string? Identity { get; init; }

    /// <summary>Files to attach: new content or attachments of messages in the mailbox.</summary>
    public IReadOnlyList<DraftAttachment> Attachments { get; init; } = [];

    /// <summary>Filenames of attachments to drop (update_draft only).</summary>
    public IReadOnlyList<string> RemoveAttachments { get; init; } = [];
}

/// <summary>One file for a draft: either <c>content_base64</c> or <c>message_id</c> + <c>part</c>.</summary>
public sealed record DraftAttachment
{
    [JsonPropertyName("filename")]
    [Description("File name shown to the recipient. Required with content_base64; renames the file when attaching from a message.")]
    public string? Filename { get; init; }

    [JsonPropertyName("content_base64")]
    [Description("The file content, base64-encoded.")]
    public string? ContentBase64 { get; init; }

    [JsonPropertyName("mime_type")]
    [Description("MIME type, e.g. application/pdf; derived from the file name when omitted.")]
    public string? MimeType { get; init; }

    [JsonPropertyName("message_id")]
    [Description("Id of a message in the same account whose attachment should be attached (instead of content_base64).")]
    public string? MessageId { get; init; }

    [JsonPropertyName("part")]
    [Description("Part of that message's attachment, from get_message's attachments list.")]
    public string? Part { get; init; }
}

public sealed record DraftAttachmentInfo(string? Filename, string Mime, long Size);

/// <summary>Builds draft MIME messages: multipart/alternative UTF-8, Message-ID on the sender's domain, reply headers, quoted original.</summary>
public static class DraftBuilder
{
    private const int MaxQuotedChars = 20_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseEmphasisExtras()
        .DisableHtml() // raw HTML in the Markdown is escaped, never passed through
        .Build();

    public static MimeMessage Build(MailAccount account, DraftRequest request, MimeMessage? original, bool isForward, DateTimeOffset now)
    {
        var from = ResolveIdentity(account, request.Identity);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(from.Name, from.Email));
        message.To.AddRange(ParseRecipients(request.To, "to"));
        message.Cc.AddRange(ParseRecipients(request.Cc, "cc"));
        message.Date = now;
        message.MessageId = MimeUtils.GenerateMessageId(DomainOf(from.Email));

        var subject = request.Subject?.Trim();
        if (original is not null)
        {
            if (isForward)
            {
                subject = string.IsNullOrEmpty(subject) ? Prefixed("Fwd: ", original.Subject) : subject;
            }
            else
            {
                subject = string.IsNullOrEmpty(subject) ? Prefixed("Re: ", original.Subject) : subject;
                if (!string.IsNullOrEmpty(original.MessageId))
                {
                    message.InReplyTo = original.MessageId;
                    foreach (var reference in original.References)
                    {
                        message.References.Add(reference);
                    }

                    message.References.Add(original.MessageId);
                }
            }
        }

        message.Subject = subject ?? string.Empty;

        var markdown = (request.Body ?? string.Empty).Replace("\r\n", "\n");
        var text = new StringBuilder(markdown);
        var html = new StringBuilder(Markdown.ToHtml(markdown, Pipeline));

        if (original is not null)
        {
            var originalText = TextTrimmer.Truncate(BodyExtractor.Extract(original).Text, MaxQuotedChars, out _);
            if (isForward)
            {
                var date = HasDate(original) ? $"Date: {original.Date:ddd, d MMM yyyy HH:mm zzz}\n" : string.Empty;
                var header = $"---------- Forwarded message ----------\nFrom: {original.From}\n{date}Subject: {original.Subject}\nTo: {original.To}\n";
                text.Append("\n\n").Append(header).Append('\n').Append(originalText);
                html.Append("<br><div>").Append(Escape(header).Replace("\n", "<br>")).Append("<br>")
                    .Append(Escape(originalText).Replace("\n", "<br>")).Append("</div>");
            }
            else
            {
                var attribution = HasDate(original)
                    ? $"On {original.Date:ddd, d MMM yyyy 'at' HH:mm}, {original.From} wrote:"
                    : $"{original.From} wrote:";
                text.Append("\n\n").Append(attribution).Append('\n');
                foreach (var line in originalText.Split('\n'))
                {
                    text.Append(line.StartsWith('>') ? ">" : "> ").Append(line).Append('\n');
                }

                html.Append("<br><div>").Append(Escape(attribution)).Append("</div><blockquote style=\"margin:0 0 0 .8ex;border-left:1px solid #ccc;padding-left:1ex\">")
                    .Append(Escape(originalText).Replace("\n", "<br>")).Append("</blockquote>");
            }
        }

        var builder = new BodyBuilder
        {
            TextBody = text.ToString().TrimEnd() + "\n",
            HtmlBody = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>" + html + "</body></html>",
        };

        if (original is not null && isForward)
        {
            foreach (var attachment in original.Attachments)
            {
                builder.Attachments.Add(attachment);
            }
        }

        message.Body = builder.ToMessageBody();
        foreach (var part in message.BodyParts.OfType<TextPart>())
        {
            part.ContentType.Charset ??= "utf-8";
        }

        return message;
    }

    /// <summary>Applies header-only changes to an existing draft (used by update_draft without a new body).</summary>
    public static MimeMessage WithHeaders(MimeMessage draft, MailAccount account, DraftRequest changes, DateTimeOffset now)
    {
        if (changes.To.Count > 0)
        {
            draft.To.Clear();
            draft.To.AddRange(ParseRecipients(changes.To, "to"));
        }

        if (changes.Cc.Count > 0)
        {
            draft.Cc.Clear();
            draft.Cc.AddRange(ParseRecipients(changes.Cc, "cc"));
        }

        if (changes.Subject is not null)
        {
            draft.Subject = changes.Subject.Trim();
        }

        if (changes.Identity is not null)
        {
            var from = ResolveIdentity(account, changes.Identity);
            draft.From.Clear();
            draft.From.Add(new MailboxAddress(from.Name, from.Email));
        }

        draft.Date = now;
        return draft;
    }

    /// <summary>Creates an attachment part from base64 content supplied by the client.</summary>
    public static MimePart CreateAttachment(string? filename, string? mimeType, string contentBase64)
    {
        var name = CleanFileName(filename)
            ?? throw new PostfachException(ErrorCodes.InvalidArgument, "Attachments with content_base64 need a filename.");

        var data = contentBase64.Trim();
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && data.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is var marker and > 0)
        {
            mimeType ??= data[5..marker];
            data = data[(marker + 8)..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, $"The content_base64 of '{name}' is not valid base64.");
        }

        if (!ContentType.TryParse(string.IsNullOrWhiteSpace(mimeType) ? MimeTypes.GetMimeType(name) : mimeType.Trim(), out var contentType)
            || contentType.MediaType.StartsWith("multipart", StringComparison.OrdinalIgnoreCase)
            || contentType.MediaType.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, $"'{mimeType}' is not a usable MIME type for '{name}'.");
        }

        return new MimePart(contentType)
        {
            Content = new MimeContent(new MemoryStream(bytes)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = name,
        };
    }

    /// <summary>Adds attachments, wrapping the body in multipart/mixed when it is not one already.</summary>
    public static void AddAttachments(MimeMessage message, IEnumerable<MimeEntity> attachments)
    {
        var list = attachments.ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (message.Body is not Multipart mixed || !mixed.ContentType.IsMimeType("multipart", "mixed"))
        {
            mixed = new Multipart("mixed");
            if (message.Body is not null)
            {
                mixed.Add(message.Body);
            }

            message.Body = mixed;
        }

        foreach (var attachment in list)
        {
            mixed.Add(attachment);
        }
    }

    /// <summary>Removes attachments by filename (case-insensitive); throws <c>not_found</c> for names the draft does not have.</summary>
    public static void RemoveAttachments(MimeMessage message, IReadOnlyCollection<string> filenames)
    {
        var wanted = filenames.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return;
        }

        var attachments = message.Attachments.ToHashSet();
        var matches = new List<(Multipart Parent, MimeEntity Entity)>();
        using (var iterator = new MimeIterator(message))
        {
            while (iterator.MoveNext())
            {
                if (iterator.Parent is Multipart parent && attachments.Contains(iterator.Current)
                    && FileNameOf(iterator.Current) is { } name && wanted.Contains(name))
                {
                    matches.Add((parent, iterator.Current));
                }
            }
        }

        var missing = wanted.Where(w => !matches.Any(m => string.Equals(FileNameOf(m.Entity), w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            throw new PostfachException(ErrorCodes.NotFound, $"The draft has no attachment named {string.Join(", ", missing.Select(m => $"'{m}'"))}.",
                "The draft preview lists its attachments.");
        }

        foreach (var (parent, entity) in matches)
        {
            parent.Remove(entity);
        }

        if (message.Body is Multipart { Count: 1 } top && top.ContentType.IsMimeType("multipart", "mixed"))
        {
            message.Body = top[0];
        }
    }

    /// <summary>Moves the attachments of <paramref name="from"/> into <paramref name="to"/>, skipping names it already has.</summary>
    public static void CarryAttachments(MimeMessage from, MimeMessage to)
    {
        var present = to.Attachments.Select(FileNameOf).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddAttachments(to, from.Attachments.Where(a => FileNameOf(a) is not { } name || !present.Contains(name)).ToList());
    }

    public static IReadOnlyList<DraftAttachmentInfo> DescribeAttachments(MimeMessage message) =>
        message.Attachments.Select(a => new DraftAttachmentInfo(FileNameOf(a), a.ContentType.MimeType.ToLowerInvariant(), DecodedSize(a))).ToList();

    public static long TotalAttachmentSize(MimeMessage message) => message.Attachments.Sum(DecodedSize);

    public static string? FileNameOf(MimeEntity entity) =>
        entity is MimePart part ? part.FileName : entity.ContentDisposition?.FileName ?? entity.ContentType.Name;

    private static long DecodedSize(MimeEntity entity)
    {
        using var stream = new MemoryStream();
        switch (entity)
        {
            case MimePart { Content: { } content }:
                content.DecodeTo(stream);
                break;
            case MessagePart { Message: { } inner }:
                inner.WriteTo(stream);
                break;
        }

        return stream.Length;
    }

    private static string? CleanFileName(string? filename)
    {
        var name = filename?.Replace('\\', '/').Split('/').Last().Trim();
        if (string.IsNullOrEmpty(name) || name is "." or "..")
        {
            return null;
        }

        return name.Length > 200 ? name[..200] : name;
    }

    public static MailIdentity ResolveIdentity(MailAccount account, string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return account.DefaultIdentity;
        }

        return account.Identities.FirstOrDefault(i => string.Equals(i.Email, identity.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? (string.Equals(account.DefaultIdentity.Email, identity.Trim(), StringComparison.OrdinalIgnoreCase)
                ? account.DefaultIdentity
                : throw new PostfachException(ErrorCodes.InvalidArgument, $"'{identity}' is not an identity of account '{account.Id}'.",
                    "Call list_accounts for the allowed from-addresses."));
    }

    public static List<MailboxAddress> ParseRecipients(IEnumerable<string> values, string field)
    {
        var result = new List<MailboxAddress>();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!InternetAddressList.TryParse(value, out var list) || list.Mailboxes.Count() == 0
                || list.Mailboxes.Any(m => !m.Address.Contains('@') || m.Address.StartsWith('@') || m.Address.EndsWith('@')))
            {
                throw new PostfachException(ErrorCodes.InvalidArgument, $"Invalid address in '{field}'.", "Use plain addresses like name@example.com.");
            }

            result.AddRange(list.Mailboxes);
        }

        return result;
    }

    private static bool HasDate(MimeMessage message) => message.Headers.Contains(HeaderId.Date) && message.Date.Year > 1970;

    private static string Prefixed(string prefix, string? subject)
    {
        subject ??= string.Empty;
        return subject.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase) ? subject : prefix + subject;
    }

    private static string DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        return at >= 0 ? email[(at + 1)..] : "postfach.local";
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
}
