using System.Net;
using System.Text;
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
}

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
