using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Postfach.Server.OAuth;

public sealed record OAuthClient(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, string? SecretHash, DateTimeOffset Created);

public sealed record AuthorizationCode(string ClientId, string RedirectUri, string CodeChallenge, IReadOnlyList<string> Scopes, string Resource, string Subject, DateTimeOffset Expires);

public sealed record OAuthToken(string Kind, string ClientId, string Subject, IReadOnlyList<string> Scopes, string Resource, string Family, DateTimeOffset Expires, bool Revoked, bool Replaced);

public sealed record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresIn, IReadOnlyList<string> Scopes);

/// <summary>
/// Clients (RFC 7591), authorization codes and tokens. Codes and tokens are opaque random strings; only their SHA-256
/// is stored, so a database leak does not leak usable tokens.
/// </summary>
public sealed class OAuthStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public OAuthStore(string path, TimeProvider time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _time = time;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS oauth_clients (
                client_id TEXT PRIMARY KEY,
                client_name TEXT,
                redirect_uris TEXT NOT NULL,
                secret_hash TEXT,
                created TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS oauth_codes (
                code_hash TEXT PRIMARY KEY,
                client_id TEXT NOT NULL,
                redirect_uri TEXT NOT NULL,
                challenge TEXT NOT NULL,
                scopes TEXT NOT NULL,
                resource TEXT NOT NULL,
                subject TEXT NOT NULL,
                expires TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS oauth_tokens (
                token_hash TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                client_id TEXT NOT NULL,
                subject TEXT NOT NULL,
                scopes TEXT NOT NULL,
                resource TEXT NOT NULL,
                family TEXT NOT NULL,
                expires TEXT NOT NULL,
                revoked INTEGER NOT NULL DEFAULT 0,
                replaced INTEGER NOT NULL DEFAULT 0,
                created TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS oauth_tokens_family ON oauth_tokens(family);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public static string NewSecret(string prefix) =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string Join(IEnumerable<string> scopes) => string.Join(' ', scopes);

    private static IReadOnlyList<string> SplitScopes(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public async Task<int> CountClientsAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM oauth_clients";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<(OAuthClient Client, string? Secret)> RegisterClientAsync(string? name, IReadOnlyList<string> redirectUris, bool confidential, CancellationToken ct)
    {
        var clientId = NewSecret("pfc_");
        var secret = confidential ? NewSecret("pfs_") : null;
        var client = new OAuthClient(clientId, name, redirectUris, secret is null ? null : HashOf(secret), _time.GetUtcNow());
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO oauth_clients(client_id, client_name, redirect_uris, secret_hash, created) VALUES ($id, $name, $uris, $secret, $created)";
        command.Parameters.AddWithValue("$id", clientId);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$uris", JsonSerializer.Serialize(redirectUris));
        command.Parameters.AddWithValue("$secret", (object?)client.SecretHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Iso(client.Created));
        await command.ExecuteNonQueryAsync(ct);
        return (client, secret);
    }

    public async Task<OAuthClient?> GetClientAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return null;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_name, redirect_uris, secret_hash, created FROM oauth_clients WHERE client_id = $id";
        command.Parameters.AddWithValue("$id", clientId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new OAuthClient(
            clientId,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [],
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseIso(reader.GetString(3)));
    }

    public async Task<string> CreateCodeAsync(AuthorizationCode code, CancellationToken ct)
    {
        var raw = NewSecret("pfx_");
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO oauth_codes(code_hash, client_id, redirect_uri, challenge, scopes, resource, subject, expires)
            VALUES ($hash, $client, $redirect, $challenge, $scopes, $resource, $subject, $expires)
            """;
        command.Parameters.AddWithValue("$hash", HashOf(raw));
        command.Parameters.AddWithValue("$client", code.ClientId);
        command.Parameters.AddWithValue("$redirect", code.RedirectUri);
        command.Parameters.AddWithValue("$challenge", code.CodeChallenge);
        command.Parameters.AddWithValue("$scopes", Join(code.Scopes));
        command.Parameters.AddWithValue("$resource", code.Resource);
        command.Parameters.AddWithValue("$subject", code.Subject);
        command.Parameters.AddWithValue("$expires", Iso(code.Expires));
        await command.ExecuteNonQueryAsync(ct);
        return raw;
    }

    /// <summary>Atomically marks the code used and returns it; null when unknown, expired or already used.</summary>
    public async Task<AuthorizationCode?> RedeemCodeAsync(string raw, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE oauth_codes SET used = 1 WHERE code_hash = $hash AND used = 0 AND expires > $now
            RETURNING client_id, redirect_uri, challenge, scopes, resource, subject, expires
            """;
        command.Parameters.AddWithValue("$hash", HashOf(raw));
        command.Parameters.AddWithValue("$now", Iso(_time.GetUtcNow()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new AuthorizationCode(reader.GetString(0), reader.GetString(1), reader.GetString(2), SplitScopes(reader.GetString(3)),
            reader.GetString(4), reader.GetString(5), ParseIso(reader.GetString(6)));
    }

    public async Task<IssuedTokens> IssueAsync(string clientId, string subject, IReadOnlyList<string> scopes, string resource, string? family,
        TimeSpan accessLifetime, TimeSpan refreshLifetime, CancellationToken ct)
    {
        family ??= Guid.NewGuid().ToString("N");
        var now = _time.GetUtcNow();
        var access = NewSecret("pfa_");
        var refresh = NewSecret("pfr_");
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var (kind, raw, lifetime) in new[] { ("access", access, accessLifetime), ("refresh", refresh, refreshLifetime) })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO oauth_tokens(token_hash, kind, client_id, subject, scopes, resource, family, expires, created)
                VALUES ($hash, $kind, $client, $subject, $scopes, $resource, $family, $expires, $created)
                """;
            command.Parameters.AddWithValue("$hash", HashOf(raw));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$client", clientId);
            command.Parameters.AddWithValue("$subject", subject);
            command.Parameters.AddWithValue("$scopes", Join(scopes));
            command.Parameters.AddWithValue("$resource", resource);
            command.Parameters.AddWithValue("$family", family);
            command.Parameters.AddWithValue("$expires", Iso(now + lifetime));
            command.Parameters.AddWithValue("$created", Iso(now));
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new IssuedTokens(access, refresh, (int)accessLifetime.TotalSeconds, scopes);
    }

    public async Task<OAuthToken?> FindTokenAsync(string raw, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, client_id, subject, scopes, resource, family, expires, revoked, replaced FROM oauth_tokens WHERE token_hash = $hash";
        command.Parameters.AddWithValue("$hash", HashOf(raw));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new OAuthToken(reader.GetString(0), reader.GetString(1), reader.GetString(2), SplitScopes(reader.GetString(3)), reader.GetString(4),
            reader.GetString(5), ParseIso(reader.GetString(6)), reader.GetInt64(7) != 0, reader.GetInt64(8) != 0);
    }

    /// <summary>Marks a refresh token as used by rotation. Returns false if it was already rotated (a replay).</summary>
    public async Task<bool> MarkReplacedAsync(string raw, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE oauth_tokens SET replaced = 1, revoked = 1 WHERE token_hash = $hash AND kind = 'refresh' AND replaced = 0 AND revoked = 0";
        command.Parameters.AddWithValue("$hash", HashOf(raw));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RevokeFamilyAsync(string family, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE family = $family";
        command.Parameters.AddWithValue("$family", family);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RevokeAllAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE oauth_tokens SET revoked = 1 WHERE revoked = 0";
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM oauth_codes WHERE expires < $now; DELETE FROM oauth_tokens WHERE expires < $now;";
        command.Parameters.AddWithValue("$now", Iso(_time.GetUtcNow()));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
