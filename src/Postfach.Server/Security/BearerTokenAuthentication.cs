using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Postfach.Server.Configuration;

namespace Postfach.Server.Security;

/// <summary>
/// Static bearer tokens (v0.1). Only SHA-256 hashes are configured; comparison is constant-time. The OAuth 2.1
/// authorization server from the shared hosting package replaces this for claude.ai connectors.
/// </summary>
public sealed class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiTokenStore tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Bearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = header["Bearer ".Length..].Trim();
        var token = tokens.Match(presented);
        if (token is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("invalid token"));
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, token.Name) };
        claims.AddRange(token.Scopes.Select(s => new Claim("scope", s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"postfach\"";
        return Task.CompletedTask;
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
