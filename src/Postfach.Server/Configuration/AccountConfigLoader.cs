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
    private sealed record Preset(string ImapHost, int ImapPort, TlsMode ImapTls, string SmtpHost, int SmtpPort, TlsMode SmtpTls, string? TokenEndpoint, string? Scope);

    private static readonly Dictionary<string, Preset> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail"] = new("imap.gmail.com", 993, TlsMode.Implicit, "smtp.gmail.com", 465, TlsMode.Implicit,
            "https://oauth2.googleapis.com/token", "https://mail.google.com/"),
        ["m365"] = new("outlook.office365.com", 993, TlsMode.Implicit, "smtp.office365.com", 587, TlsMode.StartTls,
            "https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
            "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access"),
        ["icloud"] = new("imap.mail.me.com", 993, TlsMode.Implicit, "smtp.mail.me.com", 587, TlsMode.StartTls, null, null),
        ["fastmail"] = new("imap.fastmail.com", 993, TlsMode.Implicit, "smtp.fastmail.com", 465, TlsMode.Implicit, null, null),
        ["infomaniak"] = new("mail.infomaniak.com", 993, TlsMode.Implicit, "mail.infomaniak.com", 465, TlsMode.Implicit, null, null),
    };

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

    private static MailAccount BuildAccount(AccountDto dto, SecretProtector? protector, Func<string, string?> env, List<string> warnings)
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

        var secretDto = dto.Secret ?? throw new InvalidOperationException($"Account '{id}' needs a secret.");
        AccountSecret secret;
        if (secretDto.Oauth2 is { } oauth)
        {
            var endpoint = oauth.TokenEndpoint ?? preset?.TokenEndpoint
                ?? throw new InvalidOperationException($"Account '{id}': oauth2 needs token_endpoint.");
            secret = new AccountSecret
            {
                Kind = SecretKind.OAuth2,
                TokenEndpoint = endpoint.Replace("{tenant}", oauth.Tenant ?? "common"),
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
            if (dto.Provider is "gmail" or "m365")
            {
                warnings.Add($"Account '{id}': {dto.Provider} requires OAuth (XOAUTH2); password login will likely fail.");
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

        if (dto.InsecureTls)
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
            InsecureTls = dto.InsecureTls,
            MaxSendsPerDay = dto.MaxSendsPerDay,
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
                "none" or "plain" => throw new InvalidOperationException($"Account '{id}': {kind} tls 'none' is not supported; credentials are only sent over TLS."),
                _ => throw new InvalidOperationException($"Account '{id}': unknown {kind} tls mode '{t}'."),
            };
        }
        else
        {
            tls = preset?.Tls ?? (port is 993 or 465 ? TlsMode.Implicit : TlsMode.StartTls);
        }

        return new ServerEndpoint(host.Trim(), port, tls);
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
