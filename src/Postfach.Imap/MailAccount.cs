namespace Postfach.Imap;

public enum TlsMode
{
    /// <summary>TLS from the first byte (IMAPS 993, SMTPS 465).</summary>
    Implicit,

    /// <summary>Plain connect, then STARTTLS. The connection fails if STARTTLS is not advertised.</summary>
    StartTls,

    /// <summary>
    /// No TLS. Only accepted for loopback/private hosts (local bridges such as Proton Mail Bridge or DavMail); the
    /// configuration loader enforces this.
    /// </summary>
    None,
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

    /// <summary>Provider authorization endpoint, used by the setup page's "Connect" flow.</summary>
    public string? AuthorizationEndpoint { get; init; }

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

    /// <summary>Explicit folder names for servers whose folders are neither special-use nor conventionally named.</summary>
    public IReadOnlyDictionary<FolderRole, string> FolderOverrides { get; init; } = new Dictionary<FolderRole, string>();

    /// <summary>Where the account is defined: "yaml" (read-only in the UI) or "setup" (managed on /setup).</summary>
    public string Source { get; init; } = "yaml";

    public string? Provider { get; init; }

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

    /// <summary>Raised with the account id when an account is added, changed or removed.</summary>
    event Action<string>? Changed;
}

/// <summary>Thread-safe registry whose account list can be replaced at runtime (the setup page edits accounts).</summary>
public class AccountRegistry : IAccountRegistry
{
    private volatile Snapshot _snapshot;

    public AccountRegistry(IEnumerable<MailAccount> accounts) => _snapshot = new Snapshot(accounts.ToList());

    public event Action<string>? Changed;

    public IReadOnlyList<MailAccount> Accounts => _snapshot.List;

    public MailAccount Get(string? id)
    {
        var snapshot = _snapshot;
        if (string.IsNullOrWhiteSpace(id))
        {
            return snapshot.List.Count > 0
                ? snapshot.List[0]
                : throw new PostfachException(ErrorCodes.NotFound, "No mail account is configured.", "Add one on the server's /setup page.");
        }

        return snapshot.ById.TryGetValue(id, out var account)
            ? account
            : throw new PostfachException(ErrorCodes.NotFound, $"Unknown account '{id}'.", "Call list_accounts for valid account ids.");
    }

    /// <summary>Swaps in a new account list and reports every id whose definition changed.</summary>
    public void Replace(IEnumerable<MailAccount> accounts)
    {
        var old = _snapshot;
        var next = new Snapshot(accounts.ToList());
        _snapshot = next;
        var ids = old.ById.Keys.Union(next.ById.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            old.ById.TryGetValue(id, out var before);
            next.ById.TryGetValue(id, out var after);
            if (!ReferenceEquals(before, after))
            {
                Changed?.Invoke(id);
            }
        }
    }

    private sealed class Snapshot
    {
        public Snapshot(List<MailAccount> list)
        {
            List = list;
            ById = list.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<MailAccount> List { get; }

        public Dictionary<string, MailAccount> ById { get; }
    }
}
