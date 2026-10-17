using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Postfach.Imap;
using Postfach.Server.Configuration;
using Postfach.Server.Security;

namespace Postfach.Server.Setup;

/// <summary>
/// Accounts created on the /setup page. Stored as the same <see cref="AccountDto"/> shape as postfach.yaml, with every
/// secret in its <c>*_enc</c> field (AES-256-GCM), so validation is shared with the YAML loader.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public AccountStore(string path, TimeProvider time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        _time = time;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS setup_accounts (
                id TEXT PRIMARY KEY COLLATE NOCASE,
                dto TEXT NOT NULL,
                position INTEGER NOT NULL,
                updated TEXT NOT NULL
            );
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

    public List<AccountDto> LoadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dto FROM setup_accounts ORDER BY position, id";
        using var reader = command.ExecuteReader();
        var result = new List<AccountDto>();
        while (reader.Read())
        {
            if (JsonSerializer.Deserialize<AccountDto>(reader.GetString(0), Json) is { } dto)
            {
                result.Add(dto);
            }
        }

        return result;
    }

    public AccountDto? Get(string id) =>
        LoadAll().FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    public void Save(AccountDto dto)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup_accounts(id, dto, position, updated)
            VALUES ($id, $dto, COALESCE((SELECT MAX(position) + 1 FROM setup_accounts), 0), $updated)
            ON CONFLICT(id) DO UPDATE SET dto = excluded.dto, updated = excluded.updated
            """;
        command.Parameters.AddWithValue("$id", dto.Id);
        command.Parameters.AddWithValue("$dto", JsonSerializer.Serialize(dto, Json));
        command.Parameters.AddWithValue("$updated", _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM setup_accounts WHERE id = $id; DELETE FROM oauth_refresh WHERE account = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}

/// <summary>YAML accounts (read-only) plus setup-page accounts, rebuilt whenever the setup page saves.</summary>
public sealed class AccountCatalog(
    AccountRegistry registry,
    AccountStore store,
    IReadOnlyList<MailAccount> yamlAccounts,
    SecretProtector? protector,
    ILogger<AccountCatalog> logger)
{
    private readonly Dictionary<string, (string Json, MailAccount Account)> _built = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MailAccount> YamlAccounts => yamlAccounts;

    /// <summary>Setup accounts that failed to load (e.g. the secret key changed), with the reason.</summary>
    public IReadOnlyDictionary<string, string> Broken { get; private set; } = new Dictionary<string, string>();

    public void Reload()
    {
        lock (_built)
        {
            ReloadCore();
        }
    }

    private void ReloadCore()
    {
        var accounts = new List<MailAccount>(yamlAccounts);
        var broken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in store.LoadAll())
        {
            try
            {
                if (yamlAccounts.Any(a => string.Equals(a.Id, dto.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("an account with this id is defined in postfach.yaml");
                }

                // Reuse unchanged accounts so their pooled connections survive edits to other accounts.
                var json = JsonSerializer.Serialize(dto);
                if (!_built.TryGetValue(dto.Id!, out var cached) || cached.Json != json)
                {
                    cached = (json, AccountConfigLoader.BuildAccount(dto, protector, _ => null, [], source: "setup"));
                    _built[dto.Id!] = cached;
                }

                accounts.Add(cached.Account);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                broken[dto.Id ?? "?"] = ex.Message;
                logger.LogWarning("Setup account {Account} could not be loaded: {Error}", dto.Id, ex.Message);
            }
        }

        Broken = broken;
        registry.Replace(accounts);
    }
}
