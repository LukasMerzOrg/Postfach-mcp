using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace Postfach.Imap;

/// <summary>Persists rotated OAuth refresh tokens (implemented by the server with encrypted storage).</summary>
public interface IRefreshTokenStore
{
    Task<string?> GetAsync(string accountId, CancellationToken ct);

    Task SaveAsync(string accountId, string refreshToken, CancellationToken ct);
}

public sealed class NullRefreshTokenStore : IRefreshTokenStore
{
    public Task<string?> GetAsync(string accountId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task SaveAsync(string accountId, string refreshToken, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Exchanges stored refresh tokens for short-lived access tokens (XOAUTH2 / OAUTHBEARER).</summary>
public sealed class OAuthTokenSource(HttpClient http, IRefreshTokenStore store, ILogger<OAuthTokenSource> logger)
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken ct)
    {
        if (_cache.TryGetValue(account.Id, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return cached.Token;
        }

        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(account.Id, out cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return cached.Token;
            }

            var secret = account.Secret;
            var refresh = await store.GetAsync(account.Id, ct) ?? secret.RefreshToken;
            if (string.IsNullOrEmpty(secret.TokenEndpoint) || string.IsNullOrEmpty(secret.ClientId) || string.IsNullOrEmpty(refresh))
            {
                throw new PostfachException(ErrorCodes.BackendUnavailable, $"OAuth is not fully configured for account '{account.Id}'.",
                    "The account needs token_endpoint, client_id and a refresh token.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = secret.ClientId,
            };
            if (!string.IsNullOrEmpty(secret.ClientSecret))
            {
                form["client_secret"] = secret.ClientSecret;
            }

            if (!string.IsNullOrEmpty(secret.Scope))
            {
                form["scope"] = secret.Scope;
            }

            using var response = await http.PostAsync(secret.TokenEndpoint, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OAuth refresh for account {Account} failed with HTTP {Status}", account.Id, (int)response.StatusCode);
                throw new PostfachException(ErrorCodes.BackendUnavailable, $"Could not refresh the OAuth token for account '{account.Id}'.",
                    "The mailbox authorization may have been revoked; reconnect the provider.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;
            var token = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            if (root.TryGetProperty("refresh_token", out var rotated) && rotated.GetString() is { Length: > 0 } newRefresh && newRefresh != refresh)
            {
                await store.SaveAsync(account.Id, newRefresh, ct);
            }

            _cache[account.Id] = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(string accountId) => _cache.TryRemove(accountId, out _);
}

/// <summary>Authentication shared by IMAP and SMTP clients.</summary>
public static class MailAuthenticator
{
    public static async Task AuthenticateAsync(MailKit.MailService client, MailAccount account, OAuthTokenSource tokens, CancellationToken ct)
    {
        if (!client.IsSecure)
        {
            // Never send credentials in the clear; TlsMode has no plaintext option, this is defence in depth.
            throw new PostfachException(ErrorCodes.BackendUnavailable, "Refusing to authenticate over an unencrypted connection.");
        }

        switch (account.Secret.Kind)
        {
            case SecretKind.Password:
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(new NetworkCredential(account.Username, account.Secret.Password), ct);
                break;
            case SecretKind.OAuth2:
                var token = await tokens.GetAccessTokenAsync(account, ct);
                SaslMechanism mechanism = client.AuthenticationMechanisms.Contains("OAUTHBEARER")
                    ? new SaslMechanismOAuthBearer(account.Username, token)
                    : new SaslMechanismOAuth2(account.Username, token);
                try
                {
                    await client.AuthenticateAsync(mechanism, ct);
                }
                catch (AuthenticationException)
                {
                    tokens.Invalidate(account.Id);
                    throw;
                }

                break;
        }
    }

    public static SecureSocketOptions SocketOptions(TlsMode mode) =>
        mode == TlsMode.Implicit ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
}
