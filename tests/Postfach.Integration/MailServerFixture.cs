using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Postfach.Imap;
using Postfach.Send;

namespace Postfach.Integration;

/// <summary>
/// GreenMail in Docker with two users. GreenMail speaks a minimal IMAP (no CONDSTORE, SPECIAL-USE or THREAD), so
/// folder roles come from name heuristics and flag updates take the plain STORE path.
/// </summary>
public sealed class GreenMailFixture : IAsyncLifetime
{
    public const string Image = "greenmail/standalone:2.1.3";

    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private readonly HashSet<string> _seeded = [];
    private IContainer? _container;

    public string Host => _container!.Hostname;

    public int ImapsPort => _container!.GetMappedPublicPort(3993);

    public int SmtpsPort => _container!.GetMappedPublicPort(3465);

    /// <summary>Plain IMAP, used to exercise the local-bridge (tls: none) path.</summary>
    public int ImapPlainPort => _container!.GetMappedPublicPort(3143);

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("GREENMAIL_OPTS",
                "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users.login=email "
                + "-Dgreenmail.users=alice:secret@example.com,bob:secret@example.org,carol:secret@example.net")
            .WithPortBinding(3993, true)
            .WithPortBinding(3143, true)
            .WithPortBinding(3465, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Starting GreenMail API server"))
            .Build();
        await _container.StartAsync();
        await Task.Delay(1000);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public MailAccount Account(string user, string id = "private") => new()
    {
        Id = id,
        Label = user,
        Username = user,
        Imap = new ServerEndpoint(Host, ImapsPort, TlsMode.Implicit),
        Smtp = new ServerEndpoint(Host, SmtpsPort, TlsMode.Implicit),
        Secret = new AccountSecret { Kind = SecretKind.Password, Password = "secret" },
        Identities = [new MailIdentity("Test", user)],
        InsecureTls = true, // GreenMail uses a self-signed certificate
        SentCopy = SentCopyMode.Always,
    };

    /// <summary>A raw MailKit client for seeding and for assertions that bypass Postfach.</summary>
    public async Task<ImapClient> RawClientAsync(string user)
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync(Host, ImapsPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(user, "secret");
        return client;
    }

    /// <summary>Creates Drafts/Sent/Archive/Trash and appends the corpus, once per user.</summary>
    public async Task SeedOnceAsync(string user)
    {
        await _seedGate.WaitAsync();
        try
        {
            if (_seeded.Add(user))
            {
                await SeedAsync(user);
            }
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private async Task SeedAsync(string user)
    {
        using var client = await RawClientAsync(user);
        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        foreach (var name in new[] { "Drafts", "Sent", "Archive", "Trash", "Projekte" })
        {
            if ((await personal.GetSubfoldersAsync()).All(f => f.Name != name))
            {
                await personal.CreateAsync(name, true);
            }
        }

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "corpus"), "*.eml").Order())
        {
            var message = await MimeMessage.LoadAsync(file);
            var target = Path.GetFileName(file) == "thread-sent.eml"
                ? await client.GetFolderAsync("Sent")
                : client.Inbox;
            await target.AppendAsync(new AppendRequest(message, Path.GetFileName(file) == "plain-utf8.eml" ? MessageFlags.Seen : MessageFlags.None)
            {
                InternalDate = message.Date,
            });
        }

        await client.DisconnectAsync(true);
    }
}

public sealed class InMemorySendLog : ISendLog
{
    private readonly HashSet<string> _tokens = [];

    public List<SendAuditEntry> Sends { get; } = [];

    public Task<int> CountSendsAsync(string account, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(Sends.Count(s => s.Account == account && s.Timestamp >= since));

    public Task<bool> TryConsumeTokenAsync(string tokenId, DateTimeOffset expires, CancellationToken ct) => Task.FromResult(_tokens.Add(tokenId));

    public Task RecordSendAsync(SendAuditEntry entry, CancellationToken ct)
    {
        Sends.Add(entry);
        return Task.CompletedTask;
    }
}

/// <summary>Builds a MailService/DraftService stack for one account, optionally hiding IMAP extensions.</summary>
public sealed class Stack : IAsyncDisposable
{
    public Stack(MailAccount account, SendOptions? send = null, ImapCapabilities disabled = ImapCapabilities.None)
    {
        Account = account;
        var tokens = new OAuthTokenSource(new HttpClient(), new NullRefreshTokenStore(), NullLogger<OAuthTokenSource>.Instance);
        Mail = new Postfach.Imap.MailService(new AccountRegistry([account]), new ImapConnector(tokens, NullLogger<ImapConnector>.Instance),
            NullLogger<Postfach.Imap.MailService>.Instance, new PoolOptions { DisabledCapabilities = disabled });
        SendLog = new InMemorySendLog();
        Drafts = new DraftService(Mail, new ConfirmTokens(new byte[32], TimeProvider.System), send ?? new SendOptions { AllowSend = true },
            SendLog, new SmtpSubmitter(tokens, NullLogger<SmtpSubmitter>.Instance), TimeProvider.System, NullLogger<DraftService>.Instance);
    }

    public MailAccount Account { get; }

    public Postfach.Imap.MailService Mail { get; }

    public DraftService Drafts { get; }

    public InMemorySendLog SendLog { get; }

    public ValueTask DisposeAsync() => Mail.DisposeAsync();
}
