using System.Text.RegularExpressions;
using MimeKit;
using Postfach.Imap;

namespace Postfach.Send;

public sealed class SendOptions
{
    /// <summary>Server-side master switch (<c>POSTFACH_ALLOW_SEND</c>); overrides any granted scope.</summary>
    public bool AllowSend { get; init; }

    public int MaxRecipients { get; init; } = 20;

    public int MaxSendsPerDay { get; init; } = 30;

    /// <summary>Recipient allowlist patterns such as <c>*@family.ch</c> or <c>boss@work.ch</c>. Empty = no restriction.</summary>
    public IReadOnlyList<string> Allowlist { get; init; } = [];
}

public sealed record SendAuditEntry(string Account, string? MessageId, IReadOnlyList<string> Recipients, DateTimeOffset Timestamp, string? Actor);

/// <summary>Persistence for sends (implemented by the server's SQLite store).</summary>
public interface ISendLog
{
    Task<int> CountSendsAsync(string account, DateTimeOffset since, CancellationToken ct);

    /// <summary>Marks a confirm token as used. Returns false when it was used before.</summary>
    Task<bool> TryConsumeTokenAsync(string tokenId, DateTimeOffset expires, CancellationToken ct);

    Task RecordSendAsync(SendAuditEntry entry, CancellationToken ct);
}

public static class SendPolicy
{
    public static IReadOnlyList<string> Recipients(MimeMessage message) =>
        message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .Select(m => m.Address.ToLowerInvariant())
            .Distinct()
            .ToList();

    public static void CheckRecipients(IReadOnlyList<string> recipients, SendOptions options)
    {
        if (recipients.Count == 0)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "The draft has no recipients.");
        }

        if (recipients.Count > options.MaxRecipients)
        {
            throw new PostfachException(ErrorCodes.RateLimited, $"At most {options.MaxRecipients} recipients per message.");
        }

        if (options.Allowlist.Count > 0)
        {
            var blocked = recipients.Where(r => !IsAllowed(r, options.Allowlist)).ToList();
            if (blocked.Count > 0)
            {
                throw new PostfachException(ErrorCodes.Forbidden, $"Recipient(s) not on the server's send allowlist: {string.Join(", ", blocked)}.",
                    "The server operator controls POSTFACH_SEND_ALLOWLIST.");
            }
        }
    }

    public static bool IsAllowed(string address, IReadOnlyList<string> patterns) =>
        patterns.Any(p => Regex.IsMatch(address, "^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase));
}

/// <summary>Heuristics against a prompt-injected Claude smuggling mail content out through a draft.</summary>
public static partial class ExfiltrationGuard
{
    public static IReadOnlyList<string> Check(MailAccount account, MimeMessage draft, string? newBody, bool isReplyOrForward, MimeMessage? original)
    {
        var warnings = new List<string>();
        var ownDomains = account.Identities.Select(i => Domain(i.Email))
            .Append(Domain(account.DefaultIdentity.Email))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var external = SendPolicy.Recipients(draft).Where(r => !ownDomains.Contains(Domain(r))).ToList();
        if (external.Count == 0)
        {
            return warnings;
        }

        var body = newBody ?? draft.TextBody ?? string.Empty;
        if (LongEncoded().IsMatch(body))
        {
            warnings.Add("The draft goes to an external address and contains a long encoded string (base64/hex). Check that it does not carry data out of the mailbox.");
        }

        if (LongQueryUrl().IsMatch(body))
        {
            warnings.Add("The draft contains a URL with a long query string. Check that it does not carry data out of the mailbox.");
        }

        if (!isReplyOrForward && QuoteMarker().IsMatch(body))
        {
            warnings.Add("The draft goes to an external address and quotes content from other messages. Confirm that the user wants this content shared.");
        }

        if (isReplyOrForward && original is not null)
        {
            var participants = original.From.Mailboxes.Concat(original.To.Mailboxes).Concat(original.Cc.Mailboxes).Concat(original.ReplyTo.Mailboxes)
                .Select(m => m.Address.ToLowerInvariant())
                .ToHashSet();
            var newcomers = external.Where(r => !participants.Contains(r)).ToList();
            if (newcomers.Count > 0)
            {
                warnings.Add($"The draft shares the original message with external recipient(s) who were not on it: {string.Join(", ", newcomers)}.");
            }
        }

        return warnings;
    }

    private static string Domain(string email)
    {
        var at = email.LastIndexOf('@');
        return at >= 0 ? email[(at + 1)..].ToLowerInvariant() : email.ToLowerInvariant();
    }

    [GeneratedRegex(@"(?:[A-Za-z0-9+/_-]{80,}={0,2})|(?:\b[0-9a-fA-F]{64,}\b)")]
    private static partial Regex LongEncoded();

    [GeneratedRegex(@"https?://\S+\?\S{100,}")]
    private static partial Regex LongQueryUrl();

    [GeneratedRegex(@"(?m)^(>|-{5,} ?(Original Message|Forwarded message|Ursprüngliche Nachricht|Weitergeleitete Nachricht))|\bwrote:\s*$|\bschrieb\b.*:\s*$")]
    private static partial Regex QuoteMarker();
}
