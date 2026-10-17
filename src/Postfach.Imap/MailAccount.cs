namespace Postfach.Imap;

public enum TlsMode
{
    /// <summary>TLS from the first byte (IMAPS 993, SMTPS 465).</summary>
    Implicit,

    /// <summary>Plain connect, then STARTTLS. The connection fails if STARTTLS is not advertised.</summary>
    StartTls,
}

public sealed record ServerEndpoint(string Host, int Port, TlsMode Tls);

public enum SecretKind
{
    Password,
    OAuth2,
}

/// <summary>Mailbox credentials. Lives only in memory and in encrypted storage, never in tool I/O or logs.</summary>
public sealed class AccountSecret
{
    public SecretKind Kind { get; init; }

    public string? Password { get; init; }

    /// <summary>OAuth token endpoint (e.g. https://oauth2.googleapis.com/token).</summary>
    public string? TokenEndpoint { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? RefreshToken { get; init; }

    public string? Scope { get; init; }

    public override string ToString() => $"AccountSecret({Kind}, ***)";
}

public sealed record MailIdentity(string? Name, string Email);

public enum SentCopyMode
{
    /// <summary>Append to Sent unless the provider stores submitted mail itself (Gmail, Microsoft 365).</summary>
    Auto,
    Always,
    Never,
}

public sealed class MailAccount
{
    public required string Id { get; init; }

    public string? Label { get; init; }

    public required string Username { get; init; }

    public required ServerEndpoint Imap { get; init; }

    public ServerEndpoint? Smtp { get; init; }

    public required AccountSecret Secret { get; init; }

    public IReadOnlyList<MailIdentity> Identities { get; init; } = [];

    public SentCopyMode SentCopy { get; init; } = SentCopyMode.Auto;

    /// <summary>Skip certificate validation (self-signed homelab servers only). Logged on every connection.</summary>
    public bool InsecureTls { get; init; }

    /// <summary>Daily send limit for this account, overriding the server default.</summary>
    public int? MaxSendsPerDay { get; init; }

    public MailIdentity DefaultIdentity =>
        Identities.Count > 0 ? Identities[0] : new MailIdentity(Label, Username.Contains('@') ? Username : $"{Username}@{Imap.Host}");

    public bool OwnsAddress(string email) =>
        Identities.Any(i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase));
}

public interface IAccountRegistry
{
    IReadOnlyList<MailAccount> Accounts { get; }

    /// <summary>Returns the named account, or the first configured one when <paramref name="id"/> is null.</summary>
    MailAccount Get(string? id);
}

public sealed class AccountRegistry : IAccountRegistry
{
    private readonly Dictionary<string, MailAccount> _byId;

    public AccountRegistry(IEnumerable<MailAccount> accounts)
    {
        Accounts = accounts.ToList();
        _byId = Accounts.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MailAccount> Accounts { get; }

    public MailAccount Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Accounts.Count > 0
                ? Accounts[0]
                : throw new PostfachException(ErrorCodes.NotFound, "No mail account is configured.", "Add an account to postfach.yaml.");
        }

        return _byId.TryGetValue(id, out var account)
            ? account
            : throw new PostfachException(ErrorCodes.NotFound, $"Unknown account '{id}'.", "Call list_accounts for valid account ids.");
    }
}
