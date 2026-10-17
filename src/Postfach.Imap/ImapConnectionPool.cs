using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;

namespace Postfach.Imap;

public sealed class PoolOptions
{
    public int MaxConnections { get; init; } = 3;

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Capabilities to ignore even when advertised (tests use this to exercise fallback paths).</summary>
    public ImapCapabilities DisabledCapabilities { get; init; } = ImapCapabilities.None;
}

/// <summary>Opens authenticated IMAP connections. STARTTLS is mandatory when configured (no downgrade).</summary>
public sealed class ImapConnector(OAuthTokenSource tokens, ILogger<ImapConnector> logger, bool globalInsecureTls = false)
{
    public async Task<ImapClient> ConnectAsync(MailAccount account, PoolOptions options, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = (int)options.CommandTimeout.TotalMilliseconds };
        if (account.InsecureTls || globalInsecureTls)
        {
            logger.LogWarning("TLS certificate validation is DISABLED for IMAP account {Account} ({Host})", account.Id, account.Imap.Host);
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }

        try
        {
            // SecureSocketOptions.StartTls fails when the server does not advertise STARTTLS.
            await client.ConnectAsync(account.Imap.Host, account.Imap.Port, MailAuthenticator.SocketOptions(account.Imap.Tls), ct);
            await MailAuthenticator.AuthenticateAsync(client, account, tokens, ct);
            if (options.DisabledCapabilities != ImapCapabilities.None)
            {
                client.Capabilities &= ~options.DisabledCapabilities;
            }

            if (client.Capabilities.HasFlag(ImapCapabilities.UTF8Accept))
            {
                await client.EnableUTF8Async(ct);
            }

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>A connection checked out of the pool. Dispose returns it; <see cref="MarkBroken"/> discards it instead.</summary>
public sealed class ImapLease : IAsyncDisposable
{
    private readonly ImapConnectionPool _pool;
    private bool _broken;
    private bool _returned;

    internal ImapLease(ImapConnectionPool pool, ImapClient client)
    {
        _pool = pool;
        Client = client;
    }

    public ImapClient Client { get; }

    public void MarkBroken() => _broken = true;

    public ValueTask DisposeAsync()
    {
        if (_returned)
        {
            return ValueTask.CompletedTask;
        }

        _returned = true;
        return _pool.ReturnAsync(Client, _broken);
    }
}

/// <summary>
/// Up to <see cref="PoolOptions.MaxConnections"/> authenticated connections per account. Idle connections get a NOOP
/// keepalive and are closed after <see cref="PoolOptions.IdleTimeout"/>. MailKit clients are not thread-safe, so a
/// connection is used by exactly one lease at a time.
/// </summary>
public sealed class ImapConnectionPool : IAsyncDisposable
{
    private readonly MailAccount _account;
    private readonly ImapConnector _connector;
    private readonly PoolOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<(ImapClient Client, DateTimeOffset LastUsed)> _idle = [];
    private readonly Timer _timer;
    private bool _disposed;

    public ImapConnectionPool(MailAccount account, ImapConnector connector, PoolOptions options, ILogger logger)
    {
        _account = account;
        _connector = connector;
        _options = options;
        _logger = logger;
        _slots = new SemaphoreSlim(options.MaxConnections, options.MaxConnections);
        _timer = new Timer(_ => _ = MaintainAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Capabilities seen on the most recent connection (for list_accounts).</summary>
    public ImapCapabilities? LastCapabilities { get; private set; }

    public async Task<ImapLease> AcquireAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _slots.WaitAsync(ct);
        try
        {
            while (true)
            {
                ImapClient? candidate = null;
                lock (_gate)
                {
                    if (_idle.Count > 0)
                    {
                        candidate = _idle[^1].Client;
                        _idle.RemoveAt(_idle.Count - 1);
                    }
                }

                if (candidate is null)
                {
                    break;
                }

                if (candidate.IsConnected && candidate.IsAuthenticated)
                {
                    return new ImapLease(this, candidate);
                }

                candidate.Dispose();
            }

            var client = await ConnectWithBackoffAsync(ct);
            LastCapabilities = client.Capabilities;
            return new ImapLease(this, client);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private async Task<ImapClient> ConnectWithBackoffAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _connector.ConnectAsync(_account, _options, ct);
            }
            catch (AuthenticationException ex)
            {
                _logger.LogWarning("IMAP authentication failed for account {Account}: {Error}", _account.Id, ex.GetType().Name);
                throw new PostfachException(ErrorCodes.BackendUnavailable, $"The mail server rejected the credentials for account '{_account.Id}'.",
                    "Check the stored password/app password or reconnect the provider.", ex);
            }
            catch (PostfachException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("IMAP connect attempt {Attempt} for {Account} failed: {Error}", attempt, _account.Id, ex.GetType().Name);
                await Task.Delay(delay, ct);
                delay *= 2;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("IMAP connect for {Account} failed: {Error} {Message}", _account.Id, ex.GetType().Name, ex.Message);
                throw new PostfachException(ErrorCodes.BackendUnavailable, $"Cannot reach the IMAP server for account '{_account.Id}'.",
                    "Try again later; if it persists, check host, port and TLS mode.", ex);
            }
        }
    }

    internal async ValueTask ReturnAsync(ImapClient client, bool broken)
    {
        try
        {
            if (broken || _disposed || !client.IsConnected)
            {
                await SafeDisconnectAsync(client);
                return;
            }

            lock (_gate)
            {
                _idle.Add((client, DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task MaintainAsync()
    {
        List<(ImapClient Client, DateTimeOffset LastUsed)> due;
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            due = _idle.Where(e => now - e.LastUsed >= _options.KeepAliveInterval).ToList();
            foreach (var entry in due)
            {
                _idle.Remove(entry);
            }
        }

        foreach (var (client, lastUsed) in due)
        {
            if (now - lastUsed >= _options.IdleTimeout)
            {
                await SafeDisconnectAsync(client);
                continue;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.NoOpAsync(cts.Token);
                lock (_gate)
                {
                    // Keep the original last-used time so the idle timeout still applies.
                    _idle.Add((client, lastUsed));
                }
            }
            catch (Exception)
            {
                await SafeDisconnectAsync(client);
            }
        }
    }

    private static async Task SafeDisconnectAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.DisconnectAsync(true, cts.Token);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _timer.DisposeAsync();
        List<ImapClient> clients;
        lock (_gate)
        {
            clients = _idle.Select(e => e.Client).ToList();
            _idle.Clear();
        }

        foreach (var client in clients)
        {
            await SafeDisconnectAsync(client);
        }
    }
}
