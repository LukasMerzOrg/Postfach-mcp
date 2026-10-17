using Microsoft.AspNetCore.Http;
using Postfach.Server.Configuration;

namespace Postfach.Server.OAuth;

/// <summary>A validated /oauth/authorize request (from the query string, or the consent form that echoes it).</summary>
public sealed record AuthorizeRequest(string ClientId, string RedirectUri, string CodeChallenge, IReadOnlyList<string> Scopes, string? State, string Resource)
{
    public IEnumerable<(string Key, string Value)> ToForm()
    {
        yield return ("response_type", "code");
        yield return ("client_id", ClientId);
        yield return ("redirect_uri", RedirectUri);
        yield return ("code_challenge", CodeChallenge);
        yield return ("code_challenge_method", "S256");
        yield return ("scope", string.Join(' ', Scopes));
        yield return ("resource", Resource);
        if (State is not null)
        {
            yield return ("state", State);
        }
    }

    public string ToQueryString() =>
        "?" + string.Join('&', ToForm().Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}

public abstract record AuthorizeOutcome
{
    /// <summary>The client or redirect URI cannot be trusted: show an error page, never redirect.</summary>
    public sealed record Fatal(string Message) : AuthorizeOutcome;

    /// <summary>A protocol error that is reported to the (verified) redirect URI.</summary>
    public sealed record RedirectError(string RedirectUri, string Error, string Description, string? State) : AuthorizeOutcome;

    public sealed record Valid(AuthorizeRequest Request, OAuthClient Client) : AuthorizeOutcome;
}

internal static class AuthorizeValidator
{
    public static async Task<AuthorizeOutcome> ValidateAsync(Func<string, string?> get, OAuthStore store, OAuthSettings settings, CancellationToken ct)
    {
        var client = await store.GetClientAsync(get("client_id"), ct);
        if (client is null)
        {
            return new AuthorizeOutcome.Fatal("Unknown client. The application must register first.");
        }

        var redirect = get("redirect_uri");
        if (redirect is null && client.RedirectUris.Count == 1)
        {
            redirect = client.RedirectUris[0];
        }

        if (redirect is null || !client.RedirectUris.Contains(redirect, StringComparer.Ordinal) || !settings.IsRedirectAllowed(redirect))
        {
            return new AuthorizeOutcome.Fatal("The redirect URI is not registered for this client.");
        }

        var state = get("state");
        AuthorizeOutcome Fail(string error, string description) => new AuthorizeOutcome.RedirectError(redirect, error, description, state);

        if (get("response_type") != "code")
        {
            return Fail("unsupported_response_type", "Only response_type=code is supported.");
        }

        var challenge = get("code_challenge");
        if (string.IsNullOrEmpty(challenge) || get("code_challenge_method") != "S256" || challenge.Length is < 43 or > 128)
        {
            return Fail("invalid_request", "PKCE with code_challenge_method=S256 is required.");
        }

        var resource = get("resource");
        if (resource is not null && !string.Equals(resource.TrimEnd('/'), settings.Resource, StringComparison.Ordinal))
        {
            return Fail("invalid_target", "Unknown resource.");
        }

        var requested = (get("scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        if (requested.Count == 0)
        {
            requested = [.. Configuration.Scopes.DefaultGrant];
        }

        if (requested.Except(Configuration.Scopes.All).Any())
        {
            return Fail("invalid_scope", "Unknown scope requested.");
        }

        var ordered = Configuration.Scopes.All.Where(requested.Contains).ToList();
        return new AuthorizeOutcome.Valid(new AuthorizeRequest(client.ClientId, redirect, challenge, ordered, state, settings.Resource), client);
    }

    public static string WithParams(string redirectUri, params (string Key, string? Value)[] values)
    {
        var separator = redirectUri.Contains('?') ? '&' : '?';
        return redirectUri + separator + string.Join('&', values.Where(v => v.Value is not null).Select(v => $"{v.Key}={Uri.EscapeDataString(v.Value!)}"));
    }
}
