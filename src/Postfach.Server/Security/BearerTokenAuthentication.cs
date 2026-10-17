using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Postfach.Server.Configuration;
using Postfach.Server.OAuth;

namespace Postfach.Server.Security;

/// <summary>
/// Accepts OAuth 2.1 access tokens issued by the built-in authorization server (opaque, hashed, audience-bound to
/// the /mcp resource) and static API tokens from the configuration.
/// </summary>
public sealed class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenStore tokens,
    OAuthSettings oauth,
    TimeProvider time,
    OAuthStore? oauthStore = null)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var presented = header["Bearer ".Length..].Trim();
        if (oauth.Enabled && oauthStore is not null && presented.StartsWith("pfa_", StringComparison.Ordinal))
        {
            var token = await oauthStore.FindTokenAsync(presented, Context.RequestAborted);
            if (token is null || token.Kind != "access" || token.Revoked || token.Expires <= time.GetUtcNow())
            {
                return AuthenticateResult.Fail("invalid token");
            }

            // RFC 8707: a token minted for another resource is not valid here.
            if (!string.Equals(token.Resource, oauth.Resource, StringComparison.Ordinal))
            {
                return AuthenticateResult.Fail("wrong audience");
            }

            var oauthClaims = new List<Claim>
            {
                new(ClaimTypes.Name, $"{token.Subject} via {token.ClientId[..Math.Min(12, token.ClientId.Length)]}"),
                new("sub", token.Subject),
                new("client_id", token.ClientId),
            };
            oauthClaims.AddRange(token.Scopes.Select(s => new Claim("scope", s)));
            return Success(oauthClaims);
        }

        var apiToken = tokens.Match(presented);
        if (apiToken is null)
        {
            return AuthenticateResult.Fail("invalid token");
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, apiToken.Name) };
        claims.AddRange(apiToken.Scopes.Select(s => new Claim("scope", s)));
        return Success(claims);
    }

    private static AuthenticateResult Success(List<Claim> claims) =>
        AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var value = new StringBuilder("Bearer realm=\"postfach\"");
        if (oauth.Enabled)
        {
            // RFC 9728 §5.1: tell the client where to discover the authorization server.
            value.Append($", resource_metadata=\"{oauth.Issuer}{OAuthEndpoints.ProtectedResourcePath}\", scope=\"{string.Join(' ', Scopes.DefaultGrant)}\"");
        }

        if (result.Failure is not null)
        {
            value.Append(", error=\"invalid_token\"");
        }

        Response.Headers.WWWAuthenticate = value.ToString();
    }
}

public sealed class ApiTokenStore
{
    private readonly IReadOnlyList<ApiToken> _tokens;

    public ApiTokenStore(IEnumerable<ApiToken> tokens) => _tokens = tokens.ToList();

    public int Count => _tokens.Count;

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public ApiToken? Match(string presented)
    {
        if (presented.Length is < 16 or > 512)
        {
            return null;
        }

        var hash = Hash(presented);
        ApiToken? match = null;
        foreach (var token in _tokens)
        {
            // Check all tokens so timing does not reveal which one matched.
            if (CryptographicOperations.FixedTimeEquals(hash, token.Hash))
            {
                match = token;
            }
        }

        return match;
    }
}
