using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Postfach.Imap;

public sealed record TestStep(bool Ok, string Summary, string? Hint = null, IReadOnlyList<string>? Details = null);

public sealed record AccountTestResult(TestStep Imap, TestStep? Smtp)
{
    public bool Ok => Imap.Ok && (Smtp?.Ok ?? true);
}

/// <summary>
/// Connects to an account's IMAP and SMTP servers the way Postfach will, and explains failures in plain language.
/// Nothing is sent and nothing in the mailbox is changed.
/// </summary>
public sealed class AccountTester(OAuthTokenSource tokens, ILoggerFactory? loggers = null, bool globalInsecureTls = false)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    private readonly ILoggerFactory _loggers = loggers ?? NullLoggerFactory.Instance;

    public async Task<AccountTestResult> TestAsync(MailAccount account, CancellationToken ct)
    {
        var imap = await TestImapAsync(account, ct);
        var smtp = account.Smtp is null ? null : await TestSmtpAsync(account, account.Smtp, ct);
        return new AccountTestResult(imap, smtp);
    }

    private async Task<TestStep> TestImapAsync(MailAccount account, CancellationToken ct)
    {
        var options = new PoolOptions { CommandTimeout = TimeSpan.FromSeconds(12), MaxConnections = 1 };
        var connector = new ImapConnector(tokens, _loggers.CreateLogger<ImapConnector>(), globalInsecureTls);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        ImapCapabilities capabilities;
        IReadOnlyList<string> mechanisms;
        try
        {
            using var client = await connector.ConnectAsync(account, options, timeout.Token);
            capabilities = client.Capabilities;
            mechanisms = client.AuthenticationMechanisms.ToList();
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (failure, advice) = Diagnose(ex, account, account.Imap, "IMAP");
            return new TestStep(false, failure, advice);
        }

        var details = new List<string>
        {
            $"Connected to {account.Imap.Host}:{account.Imap.Port} ({TlsLabel(account.Imap.Tls)}) and signed in as {account.Username}.",
            "Extensions: " + string.Join(", ", Interesting(capabilities)),
        };

        // Folder roles through the real code path.
        await using var mail = new MailService(new AccountRegistry([account]), connector, _loggers.CreateLogger<MailService>(), options);
        string? hint = null;
        try
        {
            var folders = await mail.ListFoldersAsync(account.Id, timeout.Token);
            details.Add($"{folders.Count} folders. " + string.Join(", ",
                folders.Where(f => f.Role is not null).Select(f => $"{f.Role}: {f.Name}")));
            var missing = new[] { "drafts", "sent", "trash" }.Where(r => folders.All(f => f.Role != r)).ToList();
            if (missing.Count > 0)
            {
                hint = $"No {string.Join("/", missing)} folder recognised. Drafts and Sent are created on first use; "
                    + "for Trash (needed by delete_messages) or unusual names, set the folder names in the account's advanced settings.";
            }
        }
        catch (PostfachException ex)
        {
            return new TestStep(false, "Signed in, but listing folders failed: " + ex.Message, ex.Hint, details);
        }

        if (!capabilities.HasFlag(ImapCapabilities.Move) && !capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            hint = (hint is null ? string.Empty : hint + " ") + "The server supports neither MOVE nor UIDPLUS, so moving and deleting are disabled for safety.";
        }

        return new TestStep(true, "IMAP works.", hint, details);
    }

    private async Task<TestStep> TestSmtpAsync(MailAccount account, ServerEndpoint endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        using var client = new SmtpClient { Timeout = 12_000 };
        if (account.InsecureTls || globalInsecureTls)
        {
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, MailAuthenticator.SocketOptions(endpoint.Tls), timeout.Token);
            if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
            {
                return new TestStep(false, $"{endpoint.Host}:{endpoint.Port} does not offer SMTP AUTH.",
                    "Use the provider's submission server (usually port 587 with STARTTLS or 465 with TLS), not port 25.");
            }

            await MailAuthenticator.AuthenticateAsync(client, account, tokens, timeout.Token, plaintextAllowed: endpoint.Tls == TlsMode.None);
            var size = client.MaxSize > 0 ? $", max message size {client.MaxSize / 1024 / 1024} MB" : string.Empty;
            await client.DisconnectAsync(true, timeout.Token);
            return new TestStep(true, "SMTP works (nothing was sent).", null,
                [$"Connected to {endpoint.Host}:{endpoint.Port} ({TlsLabel(endpoint.Tls)}) and signed in{size}."]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (summary, hint) = Diagnose(ex, account, endpoint, "SMTP");
            return new TestStep(false, summary, hint);
        }
    }

    /// <summary>Turns connection exceptions into something a person can act on.</summary>
    public static (string Summary, string? Hint) Diagnose(Exception ex, MailAccount account, ServerEndpoint endpoint, string protocol)
    {
        var where = $"{endpoint.Host}:{endpoint.Port}";
        var root = ex is PostfachException { InnerException: { } inner } ? inner : ex;
        switch (root)
        {
            case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                return ($"The host {endpoint.Host} was not found.", "Check the spelling; for your own domain the server is often imap.<domain> or mail.<domain>.");
            case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                return ($"{where} refused the connection.", protocol == "IMAP"
                    ? "IMAP usually listens on 993 (TLS) or 143 (STARTTLS)."
                    : "Submission usually listens on 587 (STARTTLS) or 465 (TLS).");
            case SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
            case OperationCanceledException:
            case TimeoutException:
                return ($"No answer from {where}.", "A firewall may block the port, or the port is wrong.");
            case SslHandshakeException ssl:
                if (endpoint.Tls == TlsMode.Implicit && endpoint.Port is 143 or 587 or 25)
                {
                    return ($"{where} does not speak TLS right away.", $"Port {endpoint.Port} normally uses STARTTLS; switch the security setting to STARTTLS.");
                }

                return ($"TLS with {where} failed: the certificate is not trusted or does not match the host name.",
                    "Use the host name the certificate was issued for (often the provider's name, not your domain). "
                    + "Only for your own server with a self-signed certificate: enable 'Allow untrusted certificate'."
                    + (ssl.InnerException is { Message: { Length: > 0 } m } ? $" ({m})" : string.Empty));
            case NotSupportedException when endpoint.Tls == TlsMode.StartTls:
                return ($"{where} does not offer STARTTLS.", "Try TLS on port 993 (IMAP) / 465 (SMTP). Postfach never sends a password unencrypted.");
            case AuthenticationException:
                return ($"The {protocol} server rejected the login for {account.Username}.", account.Secret.Kind == SecretKind.OAuth2
                    ? "The OAuth authorization may have expired or lack the IMAP/SMTP permission; connect the account again."
                    : "Check username and password. Many providers (iCloud, Yahoo, GMX, Gmail with 2FA) need an app-specific password; Gmail and Microsoft 365 need 'Connect with Google/Microsoft' instead.");
            case ImapProtocolException or SmtpProtocolException when endpoint.Tls == TlsMode.None || endpoint.Port is 993 or 465:
                return ($"Unexpected response from {where}.", "The security setting probably does not match the port (993/465 = TLS, 143/587 = STARTTLS).");
            case ImapCommandException command:
                return ($"The IMAP server refused a command: {command.Response} {command.ResponseText}".Trim(), null);
            case SmtpCommandException smtp:
                return ($"The SMTP server refused: {(int)smtp.StatusCode} {smtp.Message}", null);
            case PostfachException postfach:
                return (postfach.Message, postfach.Hint);
            default:
                return ($"{protocol} connection to {where} failed: {root.Message}", null);
        }
    }

    private static string TlsLabel(TlsMode tls) => tls switch
    {
        TlsMode.Implicit => "TLS",
        TlsMode.StartTls => "STARTTLS",
        _ => "unencrypted",
    };

    private static IEnumerable<string> Interesting(ImapCapabilities c) =>
        new[]
        {
            ImapCapabilities.IMAP4rev2, ImapCapabilities.Move, ImapCapabilities.UidPlus, ImapCapabilities.CondStore,
            ImapCapabilities.QuickResync, ImapCapabilities.Sort, ImapCapabilities.Thread, ImapCapabilities.SpecialUse,
            ImapCapabilities.UTF8Accept, ImapCapabilities.Preview, ImapCapabilities.GMailExt1, ImapCapabilities.Id,
        }.Where(i => c.HasFlag(i)).Select(i => i.ToString().ToUpperInvariant()).DefaultIfEmpty("basic IMAP4rev1 only");
}
