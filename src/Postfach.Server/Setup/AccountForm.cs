using System.Globalization;
using Microsoft.AspNetCore.Http;
using MimeKit;
using Postfach.Imap;
using Postfach.Server.Configuration;
using Postfach.Server.Security;

namespace Postfach.Server.Setup;

/// <summary>The account editor's fields. Secrets are never rendered back into the page.</summary>
public sealed class AccountForm
{
    public static readonly string[] FolderRolesEditable = ["sent", "drafts", "trash", "junk", "archive"];

    public bool IsNew { get; set; } = true;

    public string Id { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public string ImapHost { get; set; } = string.Empty;

    public int ImapPort { get; set; } = 993;

    public string ImapTls { get; set; } = "implicit";

    public bool SmtpEnabled { get; set; } = true;

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public string SmtpTls { get; set; } = "starttls";

    /// <summary>password or oauth2.</summary>
    public string Auth { get; set; } = "password";

    public bool HasStoredPassword { get; set; }

    public string? OAuthClientId { get; set; }

    public bool HasStoredClientSecret { get; set; }

    public string? OAuthTenant { get; set; }

    public string? OAuthAuthorizationEndpoint { get; set; }

    public string? OAuthTokenEndpoint { get; set; }

    public string? OAuthScope { get; set; }

    public string Identities { get; set; } = string.Empty;

    public string SentCopy { get; set; } = "auto";

    public bool InsecureTls { get; set; }

    public int? MaxSendsPerDay { get; set; }

    public Dictionary<string, string> Folders { get; set; } = [];

    public string? Note { get; set; }

    // Transient secrets from this submission (never rendered).
    public string? NewPassword { get; set; }

    public string? NewClientSecret { get; set; }

    public static AccountForm FromDiscovery(string email, DiscoveredSettings found, IEnumerable<string> takenIds)
    {
        var form = new AccountForm
        {
            Email = email,
            Username = found.Username,
            Provider = found.PresetKey,
            Label = found.ProviderName,
            ImapHost = found.Imap.Host,
            ImapPort = found.Imap.Port,
            ImapTls = Wire(found.Imap.Tls),
            SmtpEnabled = found.Smtp is not null,
            SmtpHost = found.Smtp?.Host ?? string.Empty,
            SmtpPort = found.Smtp?.Port ?? 587,
            SmtpTls = Wire(found.Smtp?.Tls ?? TlsMode.StartTls),
            Auth = found.RequiresOAuth ? "oauth2" : "password",
            InsecureTls = found.InsecureTls,
            Identities = email,
            Note = found.Note,
        };
        form.Id = SuggestId(email, takenIds);
        return form;
    }

    public static string SuggestId(string email, IEnumerable<string> taken)
    {
        var local = email.Split('@')[0].ToLowerInvariant();
        var domain = email.Contains('@') ? email.Split('@')[1].Split('.')[0].ToLowerInvariant() : "mail";
        var baseId = new string($"{local}-{domain}".Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (baseId.Length == 0)
        {
            baseId = "mail";
        }

        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        for (var i = 2; set.Contains(id); i++)
        {
            id = $"{baseId}-{i}";
        }

        return id;
    }

    public static AccountForm FromDto(AccountDto dto)
    {
        var oauth = dto.Secret?.Oauth2;
        return new AccountForm
        {
            IsNew = false,
            Id = dto.Id ?? string.Empty,
            Label = dto.Label,
            Email = dto.Identities.FirstOrDefault()?.Email ?? dto.Username ?? string.Empty,
            Username = dto.Username ?? string.Empty,
            Provider = dto.Provider,
            ImapHost = dto.Imap?.Host ?? string.Empty,
            ImapPort = dto.Imap?.Port ?? 993,
            ImapTls = dto.Imap?.Tls ?? "implicit",
            SmtpEnabled = dto.Smtp?.Host is not null,
            SmtpHost = dto.Smtp?.Host ?? string.Empty,
            SmtpPort = dto.Smtp?.Port ?? 587,
            SmtpTls = dto.Smtp?.Tls ?? "starttls",
            Auth = oauth is null ? "password" : "oauth2",
            HasStoredPassword = !string.IsNullOrEmpty(dto.Secret?.PasswordEnc),
            OAuthClientId = oauth?.ClientId,
            HasStoredClientSecret = !string.IsNullOrEmpty(oauth?.ClientSecretEnc),
            OAuthTenant = oauth?.Tenant,
            OAuthAuthorizationEndpoint = oauth?.AuthorizationEndpoint,
            OAuthTokenEndpoint = oauth?.TokenEndpoint,
            OAuthScope = oauth?.Scope,
            Identities = string.Join('\n', dto.Identities.Select(i => string.IsNullOrEmpty(i.Name) ? i.Email : $"{i.Name} <{i.Email}>")),
            SentCopy = dto.SentCopy ?? "auto",
            InsecureTls = dto.InsecureTls,
            MaxSendsPerDay = dto.MaxSendsPerDay,
            Folders = dto.Folders ?? [],
        };
    }

    public static AccountForm FromRequest(IFormCollection form)
    {
        string? S(string key) => form[key].FirstOrDefault()?.Trim() is { Length: > 0 } v ? v : null;
        int? I(string key) => int.TryParse(S(key), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new AccountForm
        {
            IsNew = S("is_new") == "1",
            Id = S("id") ?? string.Empty,
            Label = S("label"),
            Email = S("email") ?? string.Empty,
            Username = S("username") ?? S("email") ?? string.Empty,
            Provider = S("provider"),
            ImapHost = S("imap_host") ?? string.Empty,
            ImapPort = I("imap_port") ?? 993,
            ImapTls = S("imap_tls") ?? "implicit",
            SmtpEnabled = S("smtp_enabled") == "on",
            SmtpHost = S("smtp_host") ?? string.Empty,
            SmtpPort = I("smtp_port") ?? 587,
            SmtpTls = S("smtp_tls") ?? "starttls",
            Auth = S("auth") == "oauth2" ? "oauth2" : "password",
            NewPassword = form["password"].FirstOrDefault() is { Length: > 0 } p ? p : null,
            OAuthClientId = S("oauth_client_id"),
            NewClientSecret = S("oauth_client_secret"),
            OAuthTenant = S("oauth_tenant"),
            OAuthAuthorizationEndpoint = S("oauth_authorization_endpoint"),
            OAuthTokenEndpoint = S("oauth_token_endpoint"),
            OAuthScope = S("oauth_scope"),
            Identities = S("identities") ?? S("email") ?? string.Empty,
            SentCopy = S("sent_copy") ?? "auto",
            InsecureTls = S("insecure_tls") == "on",
            MaxSendsPerDay = I("max_sends_per_day"),
            Folders = FolderRolesEditable.Select(r => (r, S("folder_" + r))).Where(x => x.Item2 is not null).ToDictionary(x => x.r, x => x.Item2!),
        };
    }

    /// <summary>Builds the stored DTO. Blank secret fields keep what <paramref name="existing"/> had.</summary>
    public AccountDto ToDto(AccountDto? existing, SecretProtector protector)
    {
        var identities = new List<IdentityDto>();
        foreach (var line in Identities.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailboxAddress.TryParse(line, out var mailbox) || !mailbox.Address.Contains('@'))
            {
                throw new InvalidOperationException($"'{line}' is not a valid From address (use 'Name <address>' or just the address).");
            }

            identities.Add(new IdentityDto { Name = string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name, Email = mailbox.Address });
        }

        SecretDto secret;
        if (Auth == "oauth2")
        {
            var oldOauth = existing?.Secret?.Oauth2;
            secret = new SecretDto
            {
                Oauth2 = new OAuthDto
                {
                    ClientId = OAuthClientId,
                    ClientSecretEnc = NewClientSecret is not null ? protector.Protect(NewClientSecret) : oldOauth?.ClientSecretEnc,
                    Tenant = OAuthTenant,
                    AuthorizationEndpoint = OAuthAuthorizationEndpoint,
                    TokenEndpoint = OAuthTokenEndpoint,
                    Scope = OAuthScope,
                },
            };
        }
        else
        {
            var enc = NewPassword is not null ? protector.Protect(NewPassword) : existing?.Secret?.PasswordEnc;
            if (enc is null)
            {
                throw new InvalidOperationException("Enter the password (or app password).");
            }

            secret = new SecretDto { PasswordEnc = enc };
        }

        return new AccountDto
        {
            Id = Id,
            Label = Label,
            Provider = Provider,
            Username = string.IsNullOrWhiteSpace(Username) ? Email : Username,
            Imap = new EndpointDto { Host = ImapHost, Port = ImapPort, Tls = ImapTls },
            Smtp = SmtpEnabled && !string.IsNullOrWhiteSpace(SmtpHost) ? new EndpointDto { Host = SmtpHost, Port = SmtpPort, Tls = SmtpTls } : null,
            Secret = secret,
            Identities = identities,
            SentCopy = SentCopy,
            InsecureTls = InsecureTls,
            MaxSendsPerDay = MaxSendsPerDay,
            Folders = Folders.Count > 0 ? Folders : null,
        };
    }

    public static string Wire(TlsMode tls) => tls switch
    {
        TlsMode.Implicit => "implicit",
        TlsMode.StartTls => "starttls",
        _ => "none",
    };
}
