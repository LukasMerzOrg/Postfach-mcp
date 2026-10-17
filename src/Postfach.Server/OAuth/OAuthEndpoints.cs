using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Postfach.Send;
using Postfach.Server.Configuration;
using Postfach.Server.Storage;

namespace Postfach.Server.OAuth;

/// <summary>
/// OAuth 2.1 authorization server for MCP clients: RFC 8414 + RFC 9728 metadata, RFC 7591 dynamic registration,
/// authorization code + PKCE S256, refresh-token rotation with replay detection, RFC 8707 audience binding,
/// RFC 7009 revocation and RFC 9207 <c>iss</c>.
/// </summary>
public static class OAuthEndpoints
{
    public const string OwnerScheme = "PostfachOwner";
    public const string OidcScheme = "oidc";
    public const string ProtectedResourcePath = "/.well-known/oauth-protected-resource";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public static void MapPostfachOAuth(this WebApplication app, OAuthSettings settings)
    {
        // RFC 9728: both the root and the path-suffixed form for the /mcp resource.
        app.MapGet(ProtectedResourcePath, () => Results.Json(ProtectedResourceMetadata(settings)));
        app.MapGet(ProtectedResourcePath + "/mcp", () => Results.Json(ProtectedResourceMetadata(settings)));
        app.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(AuthorizationServerMetadata(settings)));

        var oauth = app.MapGroup("/oauth");
        oauth.MapPost("/register", RegisterAsync).RequireRateLimiting("oauth");
        oauth.MapGet("/authorize", AuthorizeAsync).RequireRateLimiting("oauth");
        oauth.MapPost("/authorize", ConsentAsync).RequireRateLimiting("oauth");
        oauth.MapPost("/login", LoginAsync).RequireRateLimiting("login");
        oauth.MapGet("/login/oidc", (HttpContext context, string? return_url) =>
            settings.OidcEnabled
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(return_url) }, [OidcScheme])
                : Results.NotFound());
        oauth.MapPost("/token", TokenAsync).RequireRateLimiting("oauth");
        oauth.MapPost("/revoke", RevokeAsync).RequireRateLimiting("oauth");
    }

    private static object ProtectedResourceMetadata(OAuthSettings settings) => new Dictionary<string, object>
    {
        ["resource"] = settings.Resource,
        ["authorization_servers"] = new[] { settings.Issuer! },
        ["scopes_supported"] = Scopes.All,
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_name"] = "Postfach",
    };

    private static object AuthorizationServerMetadata(OAuthSettings settings) => new Dictionary<string, object>
    {
        ["issuer"] = settings.Issuer!,
        ["authorization_endpoint"] = settings.Issuer + "/oauth/authorize",
        ["token_endpoint"] = settings.Issuer + "/oauth/token",
        ["registration_endpoint"] = settings.Issuer + "/oauth/register",
        ["revocation_endpoint"] = settings.Issuer + "/oauth/revoke",
        ["response_types_supported"] = new[] { "code" },
        ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
        ["code_challenge_methods_supported"] = new[] { "S256" },
        ["token_endpoint_auth_methods_supported"] = new[] { "none", "client_secret_post", "client_secret_basic" },
        ["revocation_endpoint_auth_methods_supported"] = new[] { "none", "client_secret_post", "client_secret_basic" },
        ["scopes_supported"] = Scopes.All,
        ["authorization_response_iss_parameter_supported"] = true,
    };

    private static IResult OAuthError(string error, string description, int status = 400) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private sealed record RegistrationRequest(
        string[]? redirect_uris, string? client_name, string? token_endpoint_auth_method, string[]? grant_types, string[]? response_types);

    private static async Task<IResult> RegisterAsync(HttpContext context, OAuthStore store, OAuthSettings settings, IAuditLog audit, CancellationToken ct)
    {
        RegistrationRequest? body;
        try
        {
            body = await context.Request.ReadFromJsonAsync<RegistrationRequest>(ct);
        }
        catch (JsonException)
        {
            return OAuthError("invalid_client_metadata", "Body must be JSON.");
        }

        var uris = body?.redirect_uris?.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().ToList() ?? [];
        if (uris.Count is 0 or > 10)
        {
            return OAuthError("invalid_redirect_uri", "Between 1 and 10 redirect_uris are required.");
        }

        var rejected = uris.FirstOrDefault(u => !settings.IsRedirectAllowed(u));
        if (rejected is not null)
        {
            return OAuthError("invalid_redirect_uri", $"Redirect URI not allowed by this server: {rejected}");
        }

        var method = body!.token_endpoint_auth_method ?? "none";
        if (method is not ("none" or "client_secret_post" or "client_secret_basic"))
        {
            return OAuthError("invalid_client_metadata", "Unsupported token_endpoint_auth_method.");
        }

        if (body.grant_types?.Except(["authorization_code", "refresh_token"]).Any() == true
            || body.response_types?.Except(["code"]).Any() == true)
        {
            return OAuthError("invalid_client_metadata", "Only authorization_code/refresh_token and response_type code are supported.");
        }

        if (await store.CountClientsAsync(ct) >= settings.MaxClients)
        {
            return OAuthError("invalid_client_metadata", "Client registration limit reached on this server.");
        }

        var name = body.client_name is { Length: > 0 } n ? (n.Length > 100 ? n[..100] : n) : null;
        var (client, secret) = await store.RegisterClientAsync(name, uris, method != "none", ct);
        await audit.RecordAsync("oauth_register", null, client.ClientId, $"name={name} redirects={string.Join(",", uris)}", ct);

        var response = new Dictionary<string, object?>
        {
            ["client_id"] = client.ClientId,
            ["client_id_issued_at"] = client.Created.ToUnixTimeSeconds(),
            ["client_name"] = name,
            ["redirect_uris"] = uris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = method,
        };
        if (secret is not null)
        {
            response["client_secret"] = secret;
            response["client_secret_expires_at"] = 0;
        }

        return Results.Json(response, statusCode: 201);
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext context, OAuthStore store, OAuthSettings settings, SendOptions send, IAntiforgery antiforgery, CancellationToken ct)
    {
        NoCache(context);
        var outcome = await AuthorizeValidator.ValidateAsync(k => context.Request.Query[k].FirstOrDefault(), store, settings, ct);
        switch (outcome)
        {
            case AuthorizeOutcome.Fatal fatal:
                return Results.Content(OAuthPages.Error(fatal.Message), "text/html", statusCode: 400);
            case AuthorizeOutcome.RedirectError error:
                return Results.Redirect(AuthorizeValidator.WithParams(error.RedirectUri,
                    ("error", error.Error), ("error_description", error.Description), ("state", error.State), ("iss", settings.Issuer)));
        }

        var valid = (AuthorizeOutcome.Valid)outcome;
        var tokens = antiforgery.GetAndStoreTokens(context);
        var owner = await context.AuthenticateAsync(OwnerScheme);
        if (!owner.Succeeded || owner.Principal?.Identity?.Name is not { } user)
        {
            var returnUrl = "/oauth/authorize" + valid.Request.ToQueryString();
            return Results.Content(OAuthPages.Login(returnUrl, tokens.FormFieldName, tokens.RequestToken!, settings.LocalLoginEnabled, settings.OidcEnabled, null), "text/html");
        }

        return Results.Content(OAuthPages.Consent(valid.Request, valid.Client.ClientName ?? "An MCP client", user, tokens.FormFieldName, tokens.RequestToken!, send.AllowSend), "text/html");
    }

    private static async Task<IResult> LoginAsync(HttpContext context, OAuthSettings settings, IAntiforgery antiforgery, IAuditLog audit, ILoggerFactory logs, CancellationToken ct)
    {
        NoCache(context);
        if (!await IsValidFormAsync(context, antiforgery))
        {
            return Results.Content(OAuthPages.Error("The form expired. Start again."), "text/html", statusCode: 400);
        }

        var form = await context.Request.ReadFormAsync(ct);
        var returnUrl = SafeReturnUrl(form["return_url"]);
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();

        var ok = settings.LocalLoginEnabled
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(username.ToLowerInvariant()), Encoding.UTF8.GetBytes(settings.OwnerUsername!.ToLowerInvariant()))
            & PasswordHasher.Verify(password, settings.OwnerPasswordHash!);
        if (!ok)
        {
            logs.CreateLogger("Postfach.OAuth").LogWarning("Failed owner login from {Ip}", context.Connection.RemoteIpAddress);
            await audit.RecordAsync("oauth_login_failed", null, null, $"ip={context.Connection.RemoteIpAddress}", ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500 + RandomNumberGenerator.GetInt32(500)), ct);
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Content(OAuthPages.Login(returnUrl, tokens.FormFieldName, tokens.RequestToken!, settings.LocalLoginEnabled, settings.OidcEnabled,
                "Wrong username or password."), "text/html", statusCode: 401);
        }

        await SignInOwnerAsync(context, settings.OwnerUsername!, "local");
        await audit.RecordAsync("oauth_login", null, settings.OwnerUsername, "method=local", ct);
        return Results.Redirect(returnUrl);
    }

    public static Task SignInOwnerAsync(HttpContext context, string name, string method)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim("amr", method)], OwnerScheme);
        return context.SignInAsync(OwnerScheme, new ClaimsPrincipal(identity));
    }

    private static async Task<IResult> ConsentAsync(HttpContext context, OAuthStore store, OAuthSettings settings, IAntiforgery antiforgery, IAuditLog audit, TimeProvider time, CancellationToken ct)
    {
        NoCache(context);
        var owner = await context.AuthenticateAsync(OwnerScheme);
        if (!owner.Succeeded || owner.Principal?.Identity?.Name is not { } user)
        {
            return Results.Content(OAuthPages.Error("Your sign-in expired. Start again."), "text/html", statusCode: 401);
        }

        if (!await IsValidFormAsync(context, antiforgery))
        {
            return Results.Content(OAuthPages.Error("The form expired. Start again."), "text/html", statusCode: 400);
        }

        var form = await context.Request.ReadFormAsync(ct);
        var outcome = await AuthorizeValidator.ValidateAsync(k => form[k].FirstOrDefault(), store, settings, ct);
        switch (outcome)
        {
            case AuthorizeOutcome.Fatal fatal:
                return Results.Content(OAuthPages.Error(fatal.Message), "text/html", statusCode: 400);
            case AuthorizeOutcome.RedirectError error:
                return Results.Redirect(AuthorizeValidator.WithParams(error.RedirectUri,
                    ("error", error.Error), ("error_description", error.Description), ("state", error.State), ("iss", settings.Issuer)));
        }

        var request = ((AuthorizeOutcome.Valid)outcome).Request;
        if (form["decision"] != "allow")
        {
            await audit.RecordAsync("oauth_denied", null, user, $"client={request.ClientId}", ct);
            return Results.Redirect(AuthorizeValidator.WithParams(request.RedirectUri,
                ("error", "access_denied"), ("error_description", "The user denied access."), ("state", request.State), ("iss", settings.Issuer)));
        }

        // Only scopes that were requested AND ticked are granted.
        var granted = Scopes.All.Where(s => request.Scopes.Contains(s) && form["grant"].Contains(s)).ToList();
        if (granted.Count == 0)
        {
            return Results.Redirect(AuthorizeValidator.WithParams(request.RedirectUri,
                ("error", "access_denied"), ("error_description", "No permissions were granted."), ("state", request.State), ("iss", settings.Issuer)));
        }

        var code = await store.CreateCodeAsync(new AuthorizationCode(request.ClientId, request.RedirectUri, request.CodeChallenge, granted,
            request.Resource, user, time.GetUtcNow() + CodeLifetime), ct);
        await audit.RecordAsync("oauth_consent", null, user, $"client={request.ClientId} scopes={string.Join(" ", granted)}", ct);
        return Results.Redirect(AuthorizeValidator.WithParams(request.RedirectUri, ("code", code), ("state", request.State), ("iss", settings.Issuer)));
    }

    private static async Task<IResult> TokenAsync(HttpContext context, OAuthStore store, OAuthSettings settings, IAuditLog audit, TimeProvider time, CancellationToken ct)
    {
        NoCache(context);
        if (!context.Request.HasFormContentType)
        {
            return OAuthError("invalid_request", "Use application/x-www-form-urlencoded.");
        }

        var form = await context.Request.ReadFormAsync(ct);
        var (client, clientError) = await AuthenticateClientAsync(context, form, store, ct);
        if (client is null)
        {
            return OAuthError("invalid_client", clientError!, 401);
        }

        var resource = form["resource"].FirstOrDefault();
        if (resource is not null && !string.Equals(resource.TrimEnd('/'), settings.Resource, StringComparison.Ordinal))
        {
            return OAuthError("invalid_target", "Unknown resource.");
        }

        switch (form["grant_type"].ToString())
        {
            case "authorization_code":
            {
                var raw = form["code"].ToString();
                var verifier = form["code_verifier"].ToString();
                var code = raw.Length > 0 ? await store.RedeemCodeAsync(raw, ct) : null;
                if (code is null || code.ClientId != client.ClientId)
                {
                    return OAuthError("invalid_grant", "The authorization code is invalid, expired or already used.");
                }

                if (form["redirect_uri"].FirstOrDefault() is { } redirect && redirect != code.RedirectUri)
                {
                    return OAuthError("invalid_grant", "redirect_uri does not match the authorization request.");
                }

                if (!PkceMatches(verifier, code.CodeChallenge))
                {
                    return OAuthError("invalid_grant", "PKCE verification failed.");
                }

                var issued = await store.IssueAsync(client.ClientId, code.Subject, code.Scopes, code.Resource, null,
                    settings.AccessTokenLifetime, settings.RefreshTokenLifetime, ct);
                await audit.RecordAsync("oauth_token", null, code.Subject, $"client={client.ClientId} scopes={string.Join(" ", code.Scopes)}", ct);
                return TokenResponse(issued);
            }

            case "refresh_token":
            {
                var raw = form["refresh_token"].ToString();
                var token = raw.Length > 0 ? await store.FindTokenAsync(raw, ct) : null;
                if (token is null || token.Kind != "refresh" || token.ClientId != client.ClientId)
                {
                    return OAuthError("invalid_grant", "Unknown refresh token.");
                }

                if (token.Replaced)
                {
                    // A rotated refresh token was replayed: assume theft and kill the whole grant.
                    await store.RevokeFamilyAsync(token.Family, ct);
                    await audit.RecordAsync("oauth_refresh_replay", null, token.Subject, $"client={client.ClientId} family={token.Family}", ct);
                    return OAuthError("invalid_grant", "Refresh token reuse detected; the grant was revoked.");
                }

                if (token.Revoked || token.Expires < time.GetUtcNow())
                {
                    return OAuthError("invalid_grant", "The refresh token is expired or revoked.");
                }

                var scopes = token.Scopes;
                if (form["scope"].FirstOrDefault() is { Length: > 0 } requested)
                {
                    var narrowed = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                    if (narrowed.Except(token.Scopes).Any())
                    {
                        return OAuthError("invalid_scope", "A refresh cannot widen the granted scopes.");
                    }

                    scopes = narrowed;
                }

                if (!await store.MarkReplacedAsync(raw, ct))
                {
                    await store.RevokeFamilyAsync(token.Family, ct);
                    return OAuthError("invalid_grant", "Refresh token reuse detected; the grant was revoked.");
                }

                var issued = await store.IssueAsync(client.ClientId, token.Subject, scopes, token.Resource, token.Family,
                    settings.AccessTokenLifetime, settings.RefreshTokenLifetime, ct);
                return TokenResponse(issued);
            }

            default:
                return OAuthError("unsupported_grant_type", "Use authorization_code or refresh_token.");
        }
    }

    private static async Task<IResult> RevokeAsync(HttpContext context, OAuthStore store, CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
        {
            return OAuthError("invalid_request", "Use application/x-www-form-urlencoded.");
        }

        var form = await context.Request.ReadFormAsync(ct);
        var (client, error) = await AuthenticateClientAsync(context, form, store, ct);
        if (client is null)
        {
            return OAuthError("invalid_client", error!, 401);
        }

        var raw = form["token"].ToString();
        if (raw.Length > 0 && await store.FindTokenAsync(raw, ct) is { } token && token.ClientId == client.ClientId)
        {
            await store.RevokeFamilyAsync(token.Family, ct);
        }

        // RFC 7009: unknown tokens are not an error.
        return Results.Ok();
    }

    private static async Task<(OAuthClient? Client, string? Error)> AuthenticateClientAsync(HttpContext context, IFormCollection form, OAuthStore store, CancellationToken ct)
    {
        string? clientId = form["client_id"].FirstOrDefault();
        string? secret = form["client_secret"].FirstOrDefault();
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                {
                    clientId = Uri.UnescapeDataString(decoded[..colon]);
                    secret = Uri.UnescapeDataString(decoded[(colon + 1)..]);
                }
            }
            catch (FormatException)
            {
                return (null, "Malformed Basic authorization.");
            }
        }

        var client = await store.GetClientAsync(clientId, ct);
        if (client is null)
        {
            return (null, "Unknown client.");
        }

        if (client.SecretHash is not null)
        {
            if (string.IsNullOrEmpty(secret)
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(OAuthStore.HashOf(secret)), Encoding.ASCII.GetBytes(client.SecretHash)))
            {
                return (null, "Client authentication failed.");
            }
        }

        return (client, null);
    }

    internal static bool PkceMatches(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128)
        {
            return false;
        }

        var computed = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static IResult TokenResponse(IssuedTokens issued) => Results.Json(new
    {
        access_token = issued.AccessToken,
        token_type = "Bearer",
        expires_in = issued.ExpiresIn,
        refresh_token = issued.RefreshToken,
        scope = string.Join(' ', issued.Scopes),
    });

    private static async Task<bool> IsValidFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>Only local authorize URLs are valid return targets (no open redirect).</summary>
    internal static string SafeReturnUrl(string? value) =>
        value is not null && value.StartsWith("/oauth/authorize?", StringComparison.Ordinal) && !value.Contains('\\') ? value : "/oauth/authorize";

    private static void NoCache(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' https: http://localhost:* http://127.0.0.1:*; frame-ancestors 'none'";
    }
}
