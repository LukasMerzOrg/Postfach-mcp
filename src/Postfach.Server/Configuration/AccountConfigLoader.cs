using Postfach.Imap;
using Postfach.Server.Security;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Postfach.Server.Configuration;

public sealed class ConfigFile
{
    public List<AccountDto> Accounts { get; set; } = [];

    public List<ApiTokenDto> ApiTokens { get; set; } = [];
}

public sealed class EndpointDto
{
    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? Tls { get; set; }
}

public sealed class SecretDto
{
    public string? Password { get; set; }

    public string? PasswordEnv { get; set; }

    public string? PasswordEnc { get; set; }

    public OAuthDto? Oauth2 { get; set; }
}

public sealed class OAuthDto
{
    public string? TokenEndpoint { get; set; }

    public string? Tenant { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? ClientSecretEnv { get; set; }

    public string? ClientSecretEnc { get; set; }

    public string? RefreshToken { get; set; }

    public string? RefreshTokenEnv { get; set; }

    public string? RefreshTokenEnc { get; set; }

    public string? Scope { get; set; }

    public string? AuthorizationEndpoint { get; set; }
}

public sealed class IdentityDto
{
    public string? Name { get; set; }

    public string? Email { get; set; }
}

public sealed class AccountDto
{
    public string? Id { get; set; }

    public string? Label { get; set; }

    /// <summary>Optional preset: gmail, m365, icloud, fastmail, infomaniak.</summary>
    public string? Provider { get; set; }

    public string? Username { get; set; }

    public EndpointDto? Imap { get; set; }

    public EndpointDto? Smtp { get; set; }

    public SecretDto? Secret { get; set; }

    public List<IdentityDto> Identities { get; set; } = [];

    public string? SentCopy { get; set; }

    public bool InsecureTls { get; set; }

    public int? MaxSendsPerDay { get; set; }

    /// <summary>Explicit folder names by role (sent, drafts, trash, junk, archive) for unusual servers.</summary>
    public Dictionary<string, string>? Folders { get; set; }
}

public sealed class ApiTokenDto
{
    public string? Name { get; set; }

    /// <summary>Hex SHA-256 of the token (see <c>postfach hash-token</c>); the token itself is never stored.</summary>
    public string? Sha256 { get; set; }

    public List<string> Scopes { get; set; } = [];
}

public sealed record ApiToken(string Name, byte[] Hash, IReadOnlyList<string> Scopes);

public sealed record LoadedConfig(IReadOnlyList<MailAccount> Accounts, IReadOnlyList<ApiToken> Tokens, IReadOnlyList<string> Warnings);

/// <summary>Reads <c>postfach.yaml</c>. Every secret can be given inline (discouraged), as <c>*_env</c> or as <c>*_enc</c> (AES-256-GCM).</summary>
public static class AccountConfigLoader
{
    public sealed record Preset(
        string Key, string Name, string ImapHost, int ImapPort, TlsMode ImapTls, string SmtpHost, int SmtpPort, TlsMode SmtpTls,
        string? TokenEndpoint = null, string? Scope = null, string? AuthorizationEndpoint = null, string? Note = null, bool InsecureTls = false)
    {
        public bool RequiresOAuth => TokenEndpoint is not null;
    }

    public static readonly IReadOnlyDictionary<string, Preset> Presets = new[]
    {
        new Preset("gmail", "Gmail / Google Workspace", "imap.gmail.com", 993, TlsMode.Implicit, "smtp.gmail.com", 465, TlsMode.Implicit,
            "https://oauth2.googleapis.com/token", "https://mail.google.com/", "https://accounts.google.com/o/oauth2/v2/auth",
            "Google requires OAuth: register your own Google OAuth client and use 'Connect with Google'."),
        new Preset("m365", "Microsoft 365 (work/school)", "outlook.office365.com", 993, TlsMode.Implicit, "smtp.office365.com", 587, TlsMode.StartTls,
            "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
            "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
            "Microsoft disabled password login for IMAP: register an Entra app and use 'Connect with Microsoft'."),
        new Preset("outlook", "Outlook.com / Hotmail", "outlook.office365.com", 993, TlsMode.Implicit, "smtp-mail.outlook.com", 587, TlsMode.StartTls,
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access",
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
            "Outlook.com requires OAuth: register an Entra app for personal accounts and use 'Connect with Microsoft'."),
        new Preset("icloud", "iCloud Mail", "imap.mail.me.com", 993, TlsMode.Implicit, "smtp.mail.me.com", 587, TlsMode.StartTls,
            Note: "Use an app-specific password from account.apple.com; the username is your full iCloud address."),
        new Preset("fastmail", "Fastmail", "imap.fastmail.com", 993, TlsMode.Implicit, "smtp.fastmail.com", 465, TlsMode.Implicit,
            Note: "Use an app password (Settings → Privacy & Security → Integrations)."),
        new Preset("yahoo", "Yahoo Mail", "imap.mail.yahoo.com", 993, TlsMode.Implicit, "smtp.mail.yahoo.com", 465, TlsMode.Implicit,
            Note: "Yahoo needs an app password (Account security → Generate app password)."),
        new Preset("aol", "AOL Mail", "imap.aol.com", 993, TlsMode.Implicit, "smtp.aol.com", 465, TlsMode.Implicit,
            Note: "AOL needs an app password."),
        new Preset("gmx", "GMX", "imap.gmx.net", 993, TlsMode.Implicit, "mail.gmx.net", 587, TlsMode.StartTls,
            Note: "Enable 'POP3/IMAP access' in the GMX settings first."),
        new Preset("webde", "WEB.DE", "imap.web.de", 993, TlsMode.Implicit, "smtp.web.de", 587, TlsMode.StartTls,
            Note: "Enable 'POP3/IMAP access' in the WEB.DE settings first."),
        new Preset("mailboxorg", "mailbox.org", "imap.mailbox.org", 993, TlsMode.Implicit, "smtp.mailbox.org", 465, TlsMode.Implicit),
        new Preset("posteo", "Posteo", "posteo.de", 993, TlsMode.Implicit, "posteo.de", 465, TlsMode.Implicit),
        new Preset("infomaniak", "Infomaniak", "mail.infomaniak.com", 993, TlsMode.Implicit, "mail.infomaniak.com", 465, TlsMode.Implicit),
        new Preset("bluewin", "Swisscom bluewin", "imaps.bluewin.ch", 993, TlsMode.Implicit, "smtpauths.bluewin.ch", 465, TlsMode.Implicit),
        new Preset("hostpoint", "Hostpoint", "imap.mail.hostpoint.ch", 993, TlsMode.Implicit, "asmtp.mail.hostpoint.ch", 465, TlsMode.Implicit),
        new Preset("zoho", "Zoho Mail (EU)", "imap.zoho.eu", 993, TlsMode.Implicit, "smtp.zoho.eu", 465, TlsMode.Implicit,
            Note: "Enable IMAP access in Zoho and use an app-specific password if 2FA is on."),
        new Preset("protonbridge", "Proton Mail (via Bridge)", "127.0.0.1", 1143, TlsMode.StartTls, "127.0.0.1", 1025, TlsMode.StartTls,
            Note: "Proton only offers IMAP through Proton Mail Bridge. Run the Bridge (e.g. the shenxn/protonmail-bridge container) "
                + "and use its host name, port and generated password. The Bridge uses a self-signed certificate.",
            InsecureTls: true),
    }.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static LoadedConfig Load(string path, SecretProtector? protector, Func<string, string?> env)
    {
        if (!File.Exists(path))
        {
            return new LoadedConfig([], [], [$"No config file at {path}; no accounts configured."]);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var file = deserializer.Deserialize<ConfigFile>(File.ReadAllText(path)) ?? new ConfigFile();
        return Build(file, protector, env);
    }

    public static LoadedConfig Build(ConfigFile file, SecretProtector? protector, Func<string, string?> env)
    {
        var warnings = new List<string>();
        var accounts = new List<MailAccount>();
        foreach (var dto in file.Accounts)
        {
            accounts.Add(BuildAccount(dto, protector, env, warnings));
        }

        var duplicate = accounts.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate account id '{duplicate.Key}'.");
        }

        var tokens = new List<ApiToken>();
        foreach (var t in file.ApiTokens)
        {
            if (string.IsNullOrWhiteSpace(t.Sha256) || t.Sha256.Length != 64)
            {
                throw new InvalidOperationException($"api_tokens entry '{t.Name}' needs a 64-character hex sha256.");
            }

            var unknown = t.Scopes.Except(Scopes.All).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException($"api_tokens entry '{t.Name}' has unknown scopes: {string.Join(", ", unknown)}.");
            }

            tokens.Add(new ApiToken(t.Name ?? "token", Convert.FromHexString(t.Sha256), t.Scopes.Count > 0 ? t.Scopes : Scopes.DefaultGrant));
        }

        return new LoadedConfig(accounts, tokens, warnings);
    }

    public static MailAccount BuildAccount(AccountDto dto, SecretProtector? protector, Func<string, string?> env, List<string> warnings, string source = "yaml")
    {
        var id = dto.Id?.Trim();
        if (string.IsNullOrEmpty(id) || id.Contains(':') || id.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("Every account needs an 'id' without ':' or spaces.");
        }

        if (string.IsNullOrWhiteSpace(dto.Username))
        {
            throw new InvalidOperationException($"Account '{id}' needs a username.");
        }

        Preset? preset = null;
        if (dto.Provider is { Length: > 0 } provider && !Presets.TryGetValue(provider, out preset) && provider is not ("generic" or "dovecot"))
        {
            throw new InvalidOperationException($"Account '{id}': unknown provider '{provider}'.");
        }

        var imap = Endpoint(id, "imap", dto.Imap, preset is null ? null : (preset.ImapHost, preset.ImapPort, preset.ImapTls), 993)
            ?? throw new InvalidOperationException($"Account '{id}' needs an imap host.");
        var smtp = Endpoint(id, "smtp", dto.Smtp, preset is null ? null : (preset.SmtpHost, preset.SmtpPort, preset.SmtpTls), 587);

        if (imap.Tls == TlsMode.None)
        {
            RequireLocal(id, "imap", imap.Host);
        }

        if (smtp?.Tls == TlsMode.None)
        {
            RequireLocal(id, "smtp", smtp.Host);
        }

        var secretDto = dto.Secret ?? throw new InvalidOperationException($"Account '{id}' needs a secret.");
        AccountSecret secret;
        if (secretDto.Oauth2 is { } oauth)
        {
            var endpoint = oauth.TokenEndpoint ?? preset?.TokenEndpoint
                ?? throw new InvalidOperationException($"Account '{id}': oauth2 needs token_endpoint.");
            var tenant = string.IsNullOrWhiteSpace(oauth.Tenant) ? "common" : oauth.Tenant.Trim();
            secret = new AccountSecret
            {
                Kind = SecretKind.OAuth2,
                TokenEndpoint = endpoint.Replace("{tenant}", tenant),
                AuthorizationEndpoint = (oauth.AuthorizationEndpoint ?? preset?.AuthorizationEndpoint)?.Replace("{tenant}", tenant),
                ClientId = oauth.ClientId ?? throw new InvalidOperationException($"Account '{id}': oauth2 needs client_id."),
                ClientSecret = Resolve(id, "client_secret", oauth.ClientSecret, oauth.ClientSecretEnv, oauth.ClientSecretEnc, protector, env, warnings),
                RefreshToken = Resolve(id, "refresh_token", oauth.RefreshToken, oauth.RefreshTokenEnv, oauth.RefreshTokenEnc, protector, env, warnings),
                Scope = oauth.Scope ?? preset?.Scope,
            };
        }
        else
        {
            var password = Resolve(id, "password", secretDto.Password, secretDto.PasswordEnv, secretDto.PasswordEnc, protector, env, warnings)
                ?? throw new InvalidOperationException($"Account '{id}' needs password, password_env or password_enc (or oauth2).");
            if (preset?.RequiresOAuth == true)
            {
                warnings.Add($"Account '{id}': {preset.Name} requires OAuth (XOAUTH2); password login will likely fail.");
            }

            secret = new AccountSecret { Kind = SecretKind.Password, Password = password };
        }

        var identities = dto.Identities
            .Where(i => !string.IsNullOrWhiteSpace(i.Email))
            .Select(i => new MailIdentity(i.Name, i.Email!.Trim()))
            .ToList();
        if (identities.Count == 0 && dto.Username.Contains('@'))
        {
            identities.Add(new MailIdentity(dto.Label, dto.Username.Trim()));
        }

        var folders = new Dictionary<FolderRole, string>();
        foreach (var (key, value) in dto.Folders ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!FolderRoles.TryParseWire(key, out var role) || role is FolderRole.Inbox or FolderRole.All or FolderRole.Flagged)
            {
                throw new InvalidOperationException($"Account '{id}': unknown folder role '{key}' (use sent, drafts, trash, junk or archive).");
            }

            folders[role] = value.Trim();
        }

        var insecureTls = dto.InsecureTls || (preset?.InsecureTls == true && dto.Imap?.Host is null);
        if (insecureTls)
        {
            warnings.Add($"Account '{id}': insecure_tls is on; certificate validation is disabled for this account.");
        }

        return new MailAccount
        {
            Id = id,
            Label = dto.Label,
            Username = dto.Username.Trim(),
            Imap = imap,
            Smtp = smtp,
            Secret = secret,
            Identities = identities,
            SentCopy = Enum.TryParse<SentCopyMode>(dto.SentCopy, ignoreCase: true, out var mode) ? mode : SentCopyMode.Auto,
            InsecureTls = insecureTls,
            MaxSendsPerDay = dto.MaxSendsPerDay,
            FolderOverrides = folders,
            Source = source,
            Provider = dto.Provider,
        };
    }

    private static ServerEndpoint? Endpoint(string id, string kind, EndpointDto? dto, (string Host, int Port, TlsMode Tls)? preset, int defaultPort)
    {
        var host = dto?.Host ?? preset?.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = dto?.Port ?? preset?.Port ?? defaultPort;
        TlsMode tls;
        if (dto?.Tls is { Length: > 0 } t)
        {
            tls = t.ToLowerInvariant() switch
            {
                "implicit" or "ssl" or "tls" => TlsMode.Implicit,
                "starttls" => TlsMode.StartTls,
                "none" or "plain" => TlsMode.None,
                _ => throw new InvalidOperationException($"Account '{id}': unknown {kind} tls mode '{t}'."),
            };
        }
        else
        {
            tls = preset?.Tls ?? (port is 993 or 465 ? TlsMode.Implicit : TlsMode.StartTls);
        }

        return new ServerEndpoint(host.Trim(), port, tls);
    }

    /// <summary>Unencrypted connections are only allowed to local bridges on loopback or private addresses.</summary>
    private static void RequireLocal(string id, string kind, string host)
    {
        if (!IsLocalHost(host))
        {
            throw new InvalidOperationException(
                $"Account '{id}': {kind} tls 'none' is only allowed for local bridges (localhost, private IPs or single-label container names); credentials are never sent unencrypted over the internet.");
        }
    }

    public static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            var b = ip.MapToIPv4().GetAddressBytes();
            return ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal
                || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        // A Docker container name on the same network (e.g. "protonmail-bridge") has no dots.
        return !host.Contains('.');
    }

    private static string? Resolve(string id, string name, string? plain, string? envName, string? encrypted, SecretProtector? protector, Func<string, string?> env, List<string> warnings)
    {
        if (!string.IsNullOrEmpty(encrypted))
        {
            if (protector is null)
            {
                throw new InvalidOperationException($"Account '{id}': {name}_enc needs POSTFACH_SECRET_KEY.");
            }

            return protector.Unprotect(encrypted);
        }

        if (!string.IsNullOrEmpty(envName))
        {
            return env(envName) ?? throw new InvalidOperationException($"Account '{id}': environment variable {envName} is not set.");
        }

        if (!string.IsNullOrEmpty(plain))
        {
            warnings.Add($"Account '{id}': {name} is stored in plain text; prefer {name}_env or {name}_enc.");
            return plain;
        }

        return null;
    }
}
