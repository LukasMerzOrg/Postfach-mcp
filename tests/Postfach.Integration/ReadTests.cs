using MailKit;
using MailKit.Net.Imap;
using Postfach.Imap;
using Postfach.Mime;

namespace Postfach.Integration;

[CollectionDefinition("greenmail")]
public sealed class GreenMailCollection : ICollectionFixture<GreenMailFixture>;

[Collection("greenmail")]
public sealed class ReadTests(GreenMailFixture server) : IAsyncLifetime
{
    private const string User = "alice@example.com";
    private Stack _stack = null!;

    public async Task InitializeAsync()
    {
        _stack = new Stack(server.Account(User));
        await server.SeedOnceAsync(User);
    }

    public async Task DisposeAsync() => await _stack.DisposeAsync();

    [Fact]
    public async Task Lists_folders_with_roles_from_name_heuristics()
    {
        var folders = await _stack.Mail.ListFoldersAsync(null, default);
        Assert.Contains(folders, f => f is { Name: "INBOX", Role: "inbox" } && f.Total >= 14);
        Assert.Contains(folders, f => f is { Name: "Drafts", Role: "drafts" });
        Assert.Contains(folders, f => f is { Name: "Sent", Role: "sent" });
        Assert.Contains(folders, f => f is { Name: "Trash", Role: "trash" });
        Assert.Contains(folders, f => f is { Name: "Archive", Role: "archive" });
        Assert.Contains(folders, f => f is { Name: "Projekte", Role: null });
    }

    [Theory]
    [InlineData(ImapCapabilities.None)]
    [InlineData(ImapCapabilities.Sort)]
    public async Task Searches_newest_first_with_structured_criteria(ImapCapabilities disabled)
    {
        await using var stack = new Stack(server.Account(User), disabled: disabled);
        var result = await stack.Mail.SearchAsync(null, null, new SearchCriteria { From = "bob@example.org" }, 25, null, default);
        Assert.Equal(["Re: Ausflug Samstag", "Ausflug Samstag"], result.Messages.Select(m => m.Subject));
        var first = result.Messages[0];
        Assert.Equal("inbox", first.FolderRole);
        Assert.Equal("bob@example.org", first.From?.Email);
        Assert.Contains("unread", first.Flags);
        Assert.StartsWith("Super, dann um 10 Uhr", first.Snippet);
        Assert.Equal(result.Messages[1].ThreadId, first.ThreadId);

        var unread = await stack.Mail.SearchAsync(null, "inbox", new SearchCriteria { Unread = false }, 25, null, default);
        Assert.Equal(["Abfallkalender 2027"], unread.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task Paginates_with_cursor()
    {
        var page1 = await _stack.Mail.SearchAsync(null, null, new SearchCriteria(), 5, null, default);
        Assert.Equal(5, page1.Messages.Count);
        Assert.NotNull(page1.NextCursor);
        var page2 = await _stack.Mail.SearchAsync(null, null, new SearchCriteria(), 5, page1.NextCursor, default);
        Assert.Empty(page1.Messages.Select(m => m.Id).Intersect(page2.Messages.Select(m => m.Id)));
        Assert.True(page1.Messages[^1].Date >= page2.Messages[0].Date);

        var ex = await Assert.ThrowsAsync<PostfachException>(() =>
            _stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "x" }, 5, page1.NextCursor, default));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Filters_by_attachment_and_text()
    {
        var withAttachments = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { HasAttachment = true }, 25, null, default);
        Assert.Contains(withAttachments.Messages, m => m.Subject == "Steuererklaerung Unterlagen" && m.Attachments == 2);
        Assert.DoesNotContain(withAttachments.Messages, m => m.Subject == "Abfallkalender 2027");

        var text = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { Text = "Hallwilersee" }, 25, null, default);
        Assert.Contains(text.Messages, m => m.Subject == "Ausflug Samstag");
    }

    [Fact]
    public async Task Searching_all_folders_includes_sent()
    {
        var result = await _stack.Mail.SearchAsync(null, "all", new SearchCriteria { Subject = "Ausflug" }, 25, null, default);
        Assert.Equal(3, result.Messages.Count);
        Assert.Contains(result.Messages, m => m.FolderRole == "sent");
    }

    [Fact]
    public async Task Get_message_returns_clean_body_and_does_not_mark_read()
    {
        var id = await FindAsync("Your weekly deals");
        var message = await _stack.Mail.GetMessageAsync(id, BodyFormat.Text, 8000, default);
        Assert.Contains("[weekly deals](https://shop.example/deals?utm=1)", message.UntrustedContent.Body);
        Assert.DoesNotContain("attacker", message.UntrustedContent.Body);
        Assert.Equal(3, message.UntrustedContent.HiddenTextRemoved);
        Assert.Contains("unread", message.Flags);

        var again = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "Your weekly deals" }, 5, null, default);
        Assert.Contains("unread", again.Messages.Single().Flags);
    }

    [Fact]
    public async Task Get_message_reports_auth_phishing_hints_and_attachments()
    {
        var phish = await _stack.Mail.GetMessageAsync(await FindAsync("Your account is locked"), BodyFormat.Text, 8000, default);
        Assert.Equal(new AuthResult("fail", "none", "fail"), phish.Auth);
        Assert.True(phish.SenderMismatch);
        Assert.Equal("<mailto:unsub@evil.example>", phish.ListUnsubscribe);

        var nested = await _stack.Mail.GetMessageAsync(await FindAsync("Steuererklaerung Unterlagen"), BodyFormat.Text, 8000, default);
        Assert.StartsWith("Plain version", nested.UntrustedContent.Body);
        Assert.Equal(["steuer.pdf", "archive.zip"], nested.Attachments.Select(a => a.Filename));

        var headers = await _stack.Mail.GetMessageAsync(nested.Id, BodyFormat.RawHeaders, 8000, default);
        Assert.True(headers.UntrustedContent.Body!.Contains("Message-ID: <nested-1@treuhand.example>"), headers.UntrustedContent.Body);

        var latin = await _stack.Mail.GetMessageAsync(await FindAsync("Rechnung", exact: true), BodyFormat.Text, 8000, default);
        Assert.Contains("Grüezi", latin.UntrustedContent.Body);

        // GreenMail does not decode RFC 2047 for SEARCH SUBJECT, so find it by sender.
        var byFrom = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { From = "juerg@example.ch" }, 1, null, default);
        var encoded = await _stack.Mail.GetMessageAsync(byFrom.Messages[0].Id, BodyFormat.Text, 8000, default);
        Assert.Equal("Übersicht Rechnung ✓", encoded.Subject);
        Assert.Equal("Übersicht 2026.csv", encoded.Attachments.Single().Filename);
    }

    [Fact]
    public async Task Truncates_bodies()
    {
        var message = await _stack.Mail.GetMessageAsync(await FindAsync("Vereinsnachrichten"), BodyFormat.Text, 200, default);
        Assert.True(message.UntrustedContent.Truncated);
        Assert.True(message.UntrustedContent.Body!.Length <= 202);
    }

    [Fact]
    public async Task Extracts_attachment_text_and_blocks_archives()
    {
        var id = await FindAsync("Steuererklaerung Unterlagen");
        var message = await _stack.Mail.GetMessageAsync(id, BodyFormat.Text, 8000, default);
        var pdf = await _stack.Mail.GetAttachmentAsync(id, message.Attachments[0].Part, 20_000, default);
        Assert.Contains("Steuerausweis 2026", pdf.UntrustedContent?.Text);
        var zip = await _stack.Mail.GetAttachmentAsync(id, message.Attachments[1].Part, 20_000, default);
        Assert.Equal("blocked_type", zip.Reason);
        Assert.Null(zip.UntrustedContent);

        var ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.GetAttachmentAsync(id, "9", 100, default));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Theory]
    [InlineData(ImapCapabilities.None)]
    [InlineData(ImapCapabilities.Sort | ImapCapabilities.Thread)]
    public async Task Thread_spans_inbox_and_sent_with_trimmed_bodies(ImapCapabilities disabled)
    {
        await using var stack = new Stack(server.Account(User), disabled: disabled);
        var id = await FindAsync("Ausflug Samstag", exact: true);
        var thread = await stack.Mail.GetThreadAsync(id, default);
        Assert.Equal(3, thread.Messages.Count);
        Assert.Equal(["inbox", "sent", "inbox"], thread.Messages.Select(m => m.FolderRole));
        Assert.Equal("Ja, gerne! Wann?", thread.Messages[1].UntrustedContent.Body);
        Assert.Equal("Super, dann um 10 Uhr am Bahnhof.", thread.Messages[2].UntrustedContent.Body);
    }

    [Fact]
    public async Task Stale_ids_fail_loudly()
    {
        var id = MessageRef.Parse(await FindAsync("Abfallkalender"));
        var stale = id with { UidValidity = id.UidValidity + 1 };
        var ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.GetMessageAsync(stale.ToString(), BodyFormat.Text, 100, default));
        Assert.Equal(ErrorCodes.StaleId, ex.Code);

        var missing = id with { Uid = 999_999 };
        ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.GetMessageAsync(missing.ToString(), BodyFormat.Text, 100, default));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    private async Task<string> FindAsync(string subject, bool exact = false)
    {
        var result = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = subject }, 25, null, default);
        return result.Messages.First(m => !exact || m.Subject == subject).Id;
    }
}
