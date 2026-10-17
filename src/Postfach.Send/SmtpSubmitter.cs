using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;
using Postfach.Imap;

namespace Postfach.Send;

public interface IMailSubmitter
{
    Task SubmitAsync(MailAccount account, MimeMessage message, CancellationToken ct);
}

/// <summary>SMTP submission on 587 (STARTTLS, no downgrade) or 465 (implicit TLS). AUTH is required.</summary>
public sealed class SmtpSubmitter(OAuthTokenSource tokens, ILogger<SmtpSubmitter> logger, bool globalInsecureTls = false) : IMailSubmitter
{
    public async Task SubmitAsync(MailAccount account, MimeMessage message, CancellationToken ct)
    {
        var endpoint = account.Smtp
            ?? throw new PostfachException(ErrorCodes.SendDisabled, $"Account '{account.Id}' has no SMTP server configured.");

        using var client = new SmtpClient { Timeout = 60_000 };
        if (account.InsecureTls || globalInsecureTls)
        {
            logger.LogWarning("TLS certificate validation is DISABLED for SMTP account {Account} ({Host})", account.Id, endpoint.Host);
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, MailAuthenticator.SocketOptions(endpoint.Tls), ct);
            if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            {
                throw new PostfachException(ErrorCodes.BackendUnavailable, "The SMTP server does not offer AUTH; Postfach only submits authenticated mail.");
            }

            await MailAuthenticator.AuthenticateAsync(client, account, tokens, ct);
            // MailKit never transmits the Bcc header; Bcc recipients still get the message via RCPT TO.
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (PostfachException)
        {
            throw;
        }
        catch (SmtpCommandException ex)
        {
            logger.LogWarning("SMTP submission for {Account} rejected: {Status} {Error}", account.Id, ex.StatusCode, ex.ErrorCode);
            throw new PostfachException(ErrorCodes.BackendUnavailable, $"The SMTP server rejected the message ({ex.StatusCode}).", "Nothing was sent.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("SMTP submission for {Account} failed: {Error}", account.Id, ex.GetType().Name);
            throw new PostfachException(ErrorCodes.BackendUnavailable, "Could not submit the message via SMTP.", "Nothing was sent; try again later.", ex);
        }
    }
}
