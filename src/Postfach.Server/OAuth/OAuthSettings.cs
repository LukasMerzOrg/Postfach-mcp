using System.Text.RegularExpressions;

namespace Postfach.Server.OAuth;

/// <summary>OAuth 2.1 authorization server settings (env vars). Enabled when a public URL and an owner login exist.</summary>
public sealed class OAuthSettings
{
    public static readonly IReadOnlyList<string> DefaultRedirectAllowlist =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback",
        // Native/CLI clients (Claude Code, Desktop, MCP Inspector) use loopback redirects on any port (RFC 8252).
        "http://localhost/*",
        "http://127.0.0.1/*",
        "http://[::1]/*",
    ];

    /// <summary>Issuer and base URL, e.g. https://postfach.example.com (no trailing slash).</summary>
    public string? Issuer { get; init; }

    public string? OwnerUsername { get; init; }

    /// <summary>Argon2id PHC string from <c>postfach hash-password</c>.</summary>
    public string? OwnerPasswordHash { get; init; }

    public string? OidcAuthority { get; init; }

    public string? OidcClientId { get; init; }

    public string? OidcClientSecret { get; init; }

    public IReadOnlyList<string> OidcAllowedEmails { get; init; } = [];

    public IReadOnlyList<string> OidcAllowedSubjects { get; init; } = [];

    public IReadOnlyList<string> RedirectAllowlist { get; init; } = DefaultRedirectAllowlist;

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(30);

    public int MaxClients { get; init; } = 200;

    public bool LocalLoginEnabled => !string.IsNullOrEmpty(OwnerUsername) && !string.IsNullOrEmpty(OwnerPasswordHash);

    public bool OidcEnabled => !string.IsNullOrEmpty(OidcAuthority) && !string.IsNullOrEmpty(OidcClientId);

    public bool Enabled => !string.IsNullOrEmpty(Issuer) && (LocalLoginEnabled || OidcEnabled);

    /// <summary>The protected resource (RFC 8707 audience): the MCP endpoint.</summary>
    public string Resource => Issuer + "/mcp";

    public static OAuthSettings FromEnvironment(Func<string, string?> env, string? publicUrl)
    {
        static List<string> Split(string? v) =>
            (v ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var redirects = Split(env("POSTFACH_OAUTH_REDIRECT_ALLOWLIST"));
        return new OAuthSettings
        {
            Issuer = publicUrl?.TrimEnd('/'),
            OwnerUsername = env("POSTFACH_OWNER_USERNAME"),
            OwnerPasswordHash = env("POSTFACH_OWNER_PASSWORD_HASH"),
            OidcAuthority = env("POSTFACH_OIDC_AUTHORITY"),
            OidcClientId = env("POSTFACH_OIDC_CLIENT_ID"),
            OidcClientSecret = env("POSTFACH_OIDC_CLIENT_SECRET"),
            OidcAllowedEmails = Split(env("POSTFACH_OIDC_ALLOWED_EMAILS")),
            OidcAllowedSubjects = Split(env("POSTFACH_OIDC_ALLOWED_SUBJECTS")),
            RedirectAllowlist = redirects.Count > 0 ? redirects : DefaultRedirectAllowlist,
            AccessTokenLifetime = TimeSpan.FromMinutes(int.TryParse(env("POSTFACH_OAUTH_ACCESS_TOKEN_MINUTES"), out var a) && a > 0 ? a : 60),
            RefreshTokenLifetime = TimeSpan.FromDays(int.TryParse(env("POSTFACH_OAUTH_REFRESH_TOKEN_DAYS"), out var r) && r > 0 ? r : 30),
        };
    }

    /// <summary>
    /// Exact match against the allowlist, with <c>*</c> wildcards. Loopback patterns match any port. Fragments,
    /// userinfo and non-http(s) schemes are always rejected.
    /// </summary>
    public bool IsRedirectAllowed(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.Scheme == "http" && !uri.IsLoopback)
        {
            return false;
        }

        var normalized = uri.IsLoopback
            ? $"{uri.Scheme}://{uri.Host}{uri.PathAndQuery}"
            : uri.GetLeftPart(UriPartial.Path) + uri.Query;
        foreach (var pattern in RedirectAllowlist)
        {
            var regex = "^" + Regex.Escape(pattern.TrimEnd()).Replace(@"\*", ".*") + "$";
            if (Regex.IsMatch(normalized, regex, RegexOptions.IgnoreCase) || Regex.IsMatch(candidate, regex, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
