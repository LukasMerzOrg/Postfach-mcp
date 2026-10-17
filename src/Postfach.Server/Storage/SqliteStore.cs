using System.Globalization;
using Microsoft.Data.Sqlite;
using Postfach.Imap;
using Postfach.Send;
using Postfach.Server.Security;

namespace Postfach.Server.Storage;

public interface IAuditLog
{
    Task RecordAsync(string action, string? account, string? actor, string detail, CancellationToken ct);
}

/// <summary>
/// SQLite state: audit trail (90-day retention), send counts, single-use confirm tokens and rotated OAuth refresh tokens
/// (encrypted). Message content is never written here.
/// </summary>
public sealed class SqliteStore : ISendLog, IRefreshTokenStore, IAuditLog
{
    public static readonly TimeSpan AuditRetention = TimeSpan.FromDays(90);

    private readonly string _connectionString;
    private readonly SecretProtector? _protector;
    private readonly TimeProvider _time;

    public SqliteStore(string path, SecretProtector? protector, TimeProvider time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _protector = protector;
        _time = time;
        Initialize();
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

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                action TEXT NOT NULL,
                account TEXT,
                actor TEXT,
                detail TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_ts ON audit(ts);
            CREATE TABLE IF NOT EXISTS sends (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                account TEXT NOT NULL,
                message_id TEXT,
                recipients TEXT NOT NULL,
                actor TEXT
            );
            CREATE INDEX IF NOT EXISTS sends_account_ts ON sends(account, ts);
            CREATE TABLE IF NOT EXISTS used_tokens (
                token_id TEXT PRIMARY KEY,
                expires TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS oauth_refresh (
                account TEXT PRIMARY KEY,
                token_enc TEXT NOT NULL,
                updated TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public bool IsHealthy()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public async Task RecordAsync(string action, string? account, string? actor, string detail, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit(ts, action, account, actor, detail) VALUES ($ts, $action, $account, $actor, $detail)";
        command.Parameters.AddWithValue("$ts", Iso(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$account", (object?)account ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountSendsAsync(string account, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sends WHERE account = $account AND ts >= $since";
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$since", Iso(since));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<bool> TryConsumeTokenAsync(string tokenId, DateTimeOffset expires, CancellationToken ct)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO used_tokens(token_id, expires) VALUES ($id, $expires)";
        command.Parameters.AddWithValue("$id", tokenId);
        command.Parameters.AddWithValue("$expires", Iso(expires));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task RecordSendAsync(SendAuditEntry entry, CancellationToken ct)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sends(ts, account, message_id, recipients, actor) VALUES ($ts, $account, $mid, $rcpt, $actor)";
            command.Parameters.AddWithValue("$ts", Iso(entry.Timestamp));
            command.Parameters.AddWithValue("$account", entry.Account);
            command.Parameters.AddWithValue("$mid", (object?)entry.MessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$rcpt", string.Join(",", entry.Recipients));
            command.Parameters.AddWithValue("$actor", (object?)entry.Actor ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO audit(ts, action, account, actor, detail) VALUES ($ts, 'send', $account, $actor, $detail)";
            command.Parameters.AddWithValue("$ts", Iso(entry.Timestamp));
            command.Parameters.AddWithValue("$account", entry.Account);
            command.Parameters.AddWithValue("$actor", (object?)entry.Actor ?? DBNull.Value);
            command.Parameters.AddWithValue("$detail", $"message_id={entry.MessageId} recipients={string.Join(",", entry.Recipients)}");
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<string?> GetAsync(string accountId, CancellationToken ct)
    {
        if (_protector is null)
        {
            return null;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_enc FROM oauth_refresh WHERE account = $account";
        command.Parameters.AddWithValue("$account", accountId);
        return await command.ExecuteScalarAsync(ct) is string enc ? _protector.Unprotect(enc) : null;
    }

    public async Task SaveAsync(string accountId, string refreshToken, CancellationToken ct)
    {
        if (_protector is null)
        {
            // Without a key we cannot store it safely; the configured refresh token keeps being used.
            return;
        }

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO oauth_refresh(account, token_enc, updated) VALUES ($account, $enc, $ts)
            ON CONFLICT(account) DO UPDATE SET token_enc = excluded.token_enc, updated = excluded.updated
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$enc", _protector.Protect(refreshToken));
        command.Parameters.AddWithValue("$ts", Iso(_time.GetUtcNow()));
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Deletes audit rows older than 90 days and expired single-use tokens.</summary>
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM audit WHERE ts < $cutoff;
            DELETE FROM sends WHERE ts < $cutoff;
            DELETE FROM used_tokens WHERE expires < $now;
            """;
        command.Parameters.AddWithValue("$cutoff", Iso(now - AuditRetention));
        command.Parameters.AddWithValue("$now", Iso(now));
        return await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Daily retention cleanup.</summary>
public sealed class RetentionService(SqliteStore store, ILogger<RetentionService> logger, Postfach.Server.OAuth.OAuthStore? oauth = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var removed = await store.PruneAsync(stoppingToken);
                if (oauth is not null)
                {
                    removed += await oauth.PruneAsync(stoppingToken);
                }

                logger.LogInformation("Retention cleanup removed {Rows} rows", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
