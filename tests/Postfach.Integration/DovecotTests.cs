using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Postfach.Imap;
using Postfach.Mime;
using Postfach.Send;

namespace Postfach.Integration;

/// <summary>Dovecot with the full extension set: SPECIAL-USE, CONDSTORE/QRESYNC, MOVE, SORT, THREAD=REFERENCES.</summary>
public sealed class DovecotFixture : IAsyncLifetime
{
    public const string Image = "dovecot/dovecot:2.3.21";
    public const string Password = "pass";

    private IContainer? _container;

    public string Host => _container!.Hostname;

    public int ImapsPort => _container!.GetMappedPublicPort(993);

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, "dovecot", "dovecot.conf")), "/etc/dovecot/")
            .WithPortBinding(993, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("starting up for imap"))
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

    public MailAccount Account(string user) => new()
    {
        Id = "dovecot",
        Username = user,
        Imap = new ServerEndpoint(Host, ImapsPort, TlsMode.Implicit),
        Secret = new AccountSecret { Kind = SecretKind.Password, Password = Password },
        Identities = [new MailIdentity("Test", user)],
        InsecureTls = true,
    };

    public async Task<ImapClient> RawClientAsync(string user)
    {
        var client = new ImapClient { ServerCertificateValidationCallback = (_, _, _, _) => true };
        await client.ConnectAsync(Host, ImapsPort, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(user, Password);
        return client;
    }

    public async Task SeedAsync(string user)
    {
        using var client = await RawClientAsync(user);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "corpus"), "*.eml").Order())
        {
            var message = await MimeMessage.LoadAsync(file);
            IMailFolder target = Path.GetFileName(file) == "thread-sent.eml" ? client.GetFolder(SpecialFolder.Sent)! : client.Inbox;
            await target.AppendAsync(new AppendRequest(message, MessageFlags.None) { InternalDate = message.Date });
        }

        await client.DisconnectAsync(true);
    }
}

public sealed class DovecotTests(DovecotFixture server) : IClassFixture<DovecotFixture>
{
    private async Task<Stack> StackAsync(ImapCapabilities disabled = ImapCapabilities.None)
    {
        var user = "user" + Guid.NewGuid().ToString("N")[..8];
        await server.SeedAsync(user);
        return new Stack(server.Account(user), new SendOptions(), disabled);
    }

    [Fact]
    public async Task Advertises_the_full_extension_set()
    {
        await using var stack = await StackAsync();
        await stack.Mail.ListFoldersAsync(null, default);
        var caps = stack.Mail.KnownCapabilities("dovecot") ?? ImapCapabilities.None;
        foreach (var expected in new[] { ImapCapabilities.CondStore, ImapCapabilities.QuickResync, ImapCapabilities.Move, ImapCapabilities.Sort, ImapCapabilities.Thread, ImapCapabilities.SpecialUse, ImapCapabilities.UidPlus })
        {
            Assert.True(caps.HasFlag(expected), $"missing {expected}");
        }
    }

    [Fact]
    public async Task Special_use_roles_beat_names()
    {
        await using var stack = await StackAsync();
        var folders = await stack.Mail.ListFoldersAsync(null, default);
        Assert.Contains(folders, f => f is { Name: "Gesendete Objekte", Role: "sent" });
        Assert.Contains(folders, f => f is { Name: "Archiv", Role: "archive" });
        Assert.Contains(folders, f => f is { Name: "Papierkorb", Role: "trash" });
        Assert.Contains(folders, f => f is { Name: "Spam", Role: "junk" });
        Assert.Contains(folders, f => f is { Name: "INBOX", Role: "inbox", Total: 14 });
    }

    [Fact]
    public async Task Sorted_search_and_full_text()
    {
        await using var stack = await StackAsync();
        var bob = await stack.Mail.SearchAsync(null, null, new SearchCriteria { From = "bob" }, 25, null, default);
        Assert.Equal(["Re: Ausflug Samstag", "Ausflug Samstag"], bob.Messages.Select(m => m.Subject));
        var subject = await stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "Übersicht" }, 25, null, default);
        Assert.Equal("Übersicht Rechnung ✓", subject.Messages.Single().Subject);
        var text = await stack.Mail.SearchAsync(null, null, new SearchCriteria { Text = "Hallwilersee" }, 25, null, default);
        Assert.Contains(text.Messages, m => m.Subject == "Ausflug Samstag");
        var sent = await stack.Mail.SearchAsync(null, "sent", new SearchCriteria(), 25, null, default);
        Assert.Equal("sent", sent.Messages.Single().FolderRole);
    }

    [Fact]
    public async Task Nested_parts_are_fetched_individually()
    {
        await using var stack = await StackAsync();
        var hit = await stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "Steuererklaerung" }, 1, null, default);
        var message = await stack.Mail.GetMessageAsync(hit.Messages[0].Id, BodyFormat.Text, 8000, default);
        Assert.StartsWith("Plain version", message.UntrustedContent.Body);
        var pdf = await stack.Mail.GetAttachmentAsync(message.Id, message.Attachments[0].Part, 1000, default);
        Assert.Contains("Steuerausweis", pdf.UntrustedContent?.Text);
        Assert.Contains("unread", message.Flags);
    }

    [Theory]
    [InlineData(ImapCapabilities.None)]
    [InlineData(ImapCapabilities.Thread)]
    public async Task Threads_with_and_without_thread_references(ImapCapabilities disabled)
    {
        await using var stack = await StackAsync(disabled);
        var hit = await stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "Ausflug" }, 5, null, default);
        var root = hit.Messages.Single(m => m.Subject == "Ausflug Samstag");
        var thread = await stack.Mail.GetThreadAsync(root.Id, default);
        Assert.Equal(disabled == ImapCapabilities.None ? "thread_references" : "references", thread.Method);
        Assert.Equal(["inbox", "sent", "inbox"], thread.Messages.Select(m => m.FolderRole));
    }

    [Fact]
    public async Task Condstore_flag_updates_and_move_to_special_use_archive()
    {
        await using var stack = await StackAsync();
        var hit = await stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "Abfallkalender" }, 1, null, default);
        var id = hit.Messages[0].Id;
        var result = await stack.Mail.UpdateFlagsAsync([id], ["read", "flagged"], null, default);
        Assert.Equal(1, result.Updated);
        Assert.Null(result.Conflicts);

        var moved = await stack.Mail.MoveAsync([id], "archive", default);
        var archived = await stack.Mail.GetMessageAsync(moved.Moved![id]!, BodyFormat.Text, 100, default);
        Assert.Equal("archive", archived.FolderRole);
        Assert.Contains("flagged", archived.Flags);
        Assert.StartsWith("dovecot:Archiv:", archived.Id);

        var deleted = await stack.Mail.DeleteAsync([archived.Id], default);
        Assert.StartsWith("dovecot:Papierkorb:", deleted.Moved![archived.Id]);
    }

    [Fact]
    public async Task Drafts_use_appenduid()
    {
        await using var stack = await StackAsync();
        var draft = await stack.Drafts.CreateAsync(new DraftRequest { Account = "dovecot", To = ["bob@example.org"], Subject = "Hoi", Body = "Grüezi" }, default);
        Assert.StartsWith("dovecot:Drafts:", draft.Id);
        var stored = await stack.Mail.GetMessageAsync(draft.Id, BodyFormat.Text, 1000, default);
        Assert.Contains("draft", stored.Flags);
        Assert.Contains("Grüezi", stored.UntrustedContent.Body);

        var updated = await stack.Drafts.UpdateAsync(draft.Id, new DraftRequest { Account = "dovecot", Subject = "Hoi zäme" }, default);
        Assert.Equal("Hoi zäme", updated.Subject);
        var old = await Assert.ThrowsAsync<PostfachException>(() => stack.Mail.GetMessageAsync(draft.Id, BodyFormat.Text, 100, default));
        Assert.Equal(ErrorCodes.NotFound, old.Code);
    }
}
