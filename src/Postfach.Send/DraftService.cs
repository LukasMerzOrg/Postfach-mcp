using Microsoft.Extensions.Logging;
using MimeKit;
using Postfach.Imap;
using Postfach.Mime;

namespace Postfach.Send;

public sealed record DraftPreview
{
    public required string Id { get; init; }

    public required string ConfirmToken { get; init; }

    public DateTimeOffset ConfirmTokenExpiresAt { get; init; }

    public string? From { get; init; }

    public IReadOnlyList<string> To { get; init; } = [];

    public IReadOnlyList<string> Cc { get; init; } = [];

    public string? Subject { get; init; }

    /// <summary>First 500 characters of the text body.</summary>
    public string? Preview { get; init; }

    public int Attachments { get; init; }

    public bool SendEnabled { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public required string Instructions { get; init; }
}

public sealed record SendResult
{
    public bool Sent { get; init; }

    public string? MessageId { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>appended | provider | skipped | failed</summary>
    public string? SentCopy { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Draft → review → send. There is no other way to send mail.</summary>
public sealed class DraftService(
    MailService mail,
    ConfirmTokens tokens,
    SendOptions options,
    ISendLog sendLog,
    IMailSubmitter submitter,
    TimeProvider time,
    ILogger<DraftService> logger)
{
    private const string ReviewInstructions =
        "Show the user the recipients, subject and preview above and wait for their explicit approval before calling send_draft with this confirm_token. "
        + "The draft is also visible in the Drafts folder of their own mail client.";

    public async Task<DraftPreview> CreateAsync(DraftRequest request, CancellationToken ct)
    {
        var account = mail.Accounts.Get(request.Account);
        if (request.To.Count == 0)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "'to' needs at least one recipient.");
        }

        if (request.InReplyTo is not null && request.ForwardOf is not null)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "Use either in_reply_to or forward_of, not both.");
        }

        if (string.IsNullOrWhiteSpace(request.Subject) && request.InReplyTo is null && request.ForwardOf is null)
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "'subject' is required for a new message.");
        }

        var (original, isForward) = await LoadOriginalAsync(account, request, ct);
        var message = DraftBuilder.Build(account, request, original, isForward, time.GetUtcNow());
        var warnings = ExfiltrationGuard.Check(account, message, request.Body, original is not null, original).ToList();
        var id = await mail.AppendDraftAsync(account.Id, message, ct);
        return Preview(id, message, warnings);
    }

    public async Task<DraftPreview> UpdateAsync(string draftId, DraftRequest changes, CancellationToken ct)
    {
        var reference = MessageRef.Parse(draftId);
        var account = mail.Accounts.Get(reference.Account);
        await mail.EnsureDraftAsync(reference, ct);
        var existing = await mail.GetMimeMessageAsync(reference, ct);

        MimeMessage updated;
        MimeMessage? original = null;
        if (changes.Body is not null)
        {
            bool isForward;
            (original, isForward) = await LoadOriginalAsync(account, changes, ct);
            var merged = changes with
            {
                To = changes.To.Count > 0 ? changes.To : existing.To.Mailboxes.Select(m => m.ToString()).ToList(),
                Cc = changes.Cc.Count > 0 ? changes.Cc : existing.Cc.Mailboxes.Select(m => m.ToString()).ToList(),
                Subject = changes.Subject ?? existing.Subject,
                Identity = changes.Identity ?? existing.From.Mailboxes.FirstOrDefault()?.Address,
            };
            updated = DraftBuilder.Build(account, merged, original, isForward, time.GetUtcNow());
            if (original is null)
            {
                // Keep the reply headers of the draft being replaced.
                updated.InReplyTo = existing.InReplyTo;
                foreach (var r in existing.References)
                {
                    updated.References.Add(r);
                }
            }
        }
        else
        {
            updated = DraftBuilder.WithHeaders(existing, account, changes, time.GetUtcNow());
        }

        var warnings = ExfiltrationGuard.Check(account, updated, changes.Body, original is not null || updated.InReplyTo is not null, original).ToList();
        var newId = await mail.AppendDraftAsync(account.Id, updated, ct);
        try
        {
            await mail.DeleteDraftAsync(reference, ct);
        }
        catch (PostfachException ex)
        {
            logger.LogWarning("Could not remove the replaced draft for {Account}: {Code}", account.Id, ex.Code);
            warnings.Add("The previous version of the draft could not be removed from Drafts.");
        }

        return Preview(newId, updated, warnings);
    }

    /// <summary>Issues a fresh token for an existing draft (e.g. one the user edited in their mail client).</summary>
    public async Task<DraftPreview> PreviewAsync(string draftId, CancellationToken ct)
    {
        var reference = MessageRef.Parse(draftId);
        var account = mail.Accounts.Get(reference.Account);
        await mail.EnsureDraftAsync(reference, ct);
        var draft = await mail.GetMimeMessageAsync(reference, ct);
        return Preview(reference, draft, ExfiltrationGuard.Check(account, draft, null, draft.InReplyTo is not null, null).ToList());
    }

    public async Task<SendResult> SendAsync(string draftId, string? confirmToken, string? actor, CancellationToken ct)
    {
        // Gate 1: server switch. (Gate 2, the OAuth scope, is enforced by the MCP layer before we get here.)
        if (!options.AllowSend)
        {
            throw new PostfachException(ErrorCodes.SendDisabled, "Sending is disabled on this server.",
                "The operator can enable it with POSTFACH_ALLOW_SEND=true. The draft stays in Drafts and can be sent from a mail client.");
        }

        var reference = MessageRef.Parse(draftId);
        var account = mail.Accounts.Get(reference.Account);
        if (account.Smtp is null)
        {
            throw new PostfachException(ErrorCodes.SendDisabled, $"Account '{account.Id}' has no SMTP server configured.");
        }

        await mail.EnsureDraftAsync(reference, ct);

        // Gate 3: the token must match the draft's current content, as stored on the IMAP server.
        var draft = await mail.GetMimeMessageAsync(reference, ct);
        var tokenId = tokens.Verify(confirmToken, reference, ConfirmTokens.ContentHash(draft));

        var recipients = SendPolicy.Recipients(draft);
        SendPolicy.CheckRecipients(recipients, options);
        var from = draft.From.Mailboxes.FirstOrDefault();
        if (from is null || !(account.OwnsAddress(from.Address)
                              || string.Equals(from.Address, account.DefaultIdentity.Email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PostfachException(ErrorCodes.Forbidden, "The draft's From address is not an identity of this account.");
        }

        var limit = account.MaxSendsPerDay ?? options.MaxSendsPerDay;
        var now = time.GetUtcNow();
        if (await sendLog.CountSendsAsync(account.Id, now.AddDays(-1), ct) >= limit)
        {
            throw new PostfachException(ErrorCodes.RateLimited, $"Account '{account.Id}' reached its limit of {limit} sends per day.", "Try again tomorrow.");
        }

        if (!await sendLog.TryConsumeTokenAsync(tokenId, now.Add(ConfirmTokens.Lifetime), ct))
        {
            throw new PostfachException(ErrorCodes.TokenMismatch, "This confirm_token was already used.", "Each draft is sent once.");
        }

        draft.Date = now;
        await submitter.SubmitAsync(account, draft, ct);
        await sendLog.RecordSendAsync(new SendAuditEntry(account.Id, draft.MessageId, recipients, now, actor), ct);
        logger.LogInformation("Sent message for account {Account} to {Count} recipient(s)", account.Id, recipients.Count);

        var warnings = new List<string>();
        var sentCopy = "skipped";
        if (await ShouldAppendToSentAsync(account, ct))
        {
            try
            {
                var copy = await mail.GetMimeMessageAsync(reference, ct);
                copy.Date = now;
                await mail.AppendSentAsync(account.Id, copy, ct);
                sentCopy = "appended";
            }
            catch (PostfachException ex)
            {
                sentCopy = "failed";
                warnings.Add($"The message was sent but could not be copied to Sent ({ex.Code}).");
            }
        }
        else if (account.SentCopy == SentCopyMode.Auto)
        {
            sentCopy = "provider";
        }

        try
        {
            await mail.DeleteDraftAsync(reference, ct);
        }
        catch (PostfachException ex)
        {
            warnings.Add($"The message was sent but the draft could not be removed ({ex.Code}).");
        }

        return new SendResult { Sent = true, MessageId = draft.MessageId, Recipients = recipients, SentCopy = sentCopy, Warnings = warnings };
    }

    private async Task<bool> ShouldAppendToSentAsync(MailAccount account, CancellationToken ct)
    {
        switch (account.SentCopy)
        {
            case SentCopyMode.Always:
                return true;
            case SentCopyMode.Never:
                return false;
        }

        // Gmail and Microsoft 365 store submitted mail in Sent themselves.
        var host = account.Smtp?.Host ?? string.Empty;
        if (host.EndsWith("office365.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("outlook.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !await mail.SupportsGmailExtensionsAsync(account.Id, ct);
    }

    private async Task<(MimeMessage? Original, bool IsForward)> LoadOriginalAsync(MailAccount account, DraftRequest request, CancellationToken ct)
    {
        var sourceId = request.InReplyTo ?? request.ForwardOf;
        if (sourceId is null)
        {
            return (null, false);
        }

        var source = MessageRef.Parse(sourceId);
        if (!string.Equals(source.Account, account.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new PostfachException(ErrorCodes.InvalidArgument, "The original message must belong to the same account as the draft.");
        }

        return (await mail.GetMimeMessageAsync(source, ct), request.ForwardOf is not null);
    }

    private DraftPreview Preview(MessageRef id, MimeMessage message, IReadOnlyList<string> warnings)
    {
        var (token, expires) = tokens.Issue(id, ConfirmTokens.ContentHash(message));
        var text = BodyExtractor.Extract(message).Text;
        var preview = text.Length > 500 ? text[..500] + "…" : text;
        var recipientCount = SendPolicy.Recipients(message).Count;
        var allWarnings = warnings.ToList();
        if (recipientCount > options.MaxRecipients)
        {
            allWarnings.Add($"The draft has {recipientCount} recipients; the server sends to at most {options.MaxRecipients}.");
        }

        return new DraftPreview
        {
            Id = id.ToString(),
            ConfirmToken = token,
            ConfirmTokenExpiresAt = expires,
            From = message.From.Mailboxes.FirstOrDefault()?.ToString(),
            To = message.To.Mailboxes.Select(m => m.ToString()).ToList(),
            Cc = message.Cc.Mailboxes.Select(m => m.ToString()).ToList(),
            Subject = message.Subject,
            Preview = preview,
            Attachments = message.Attachments.Count(),
            SendEnabled = options.AllowSend,
            Warnings = allWarnings,
            Instructions = options.AllowSend
                ? ReviewInstructions
                : "Sending is disabled on this server; the user can review and send the draft from their own mail client.",
        };
    }
}
