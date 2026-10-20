using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Postfach.Imap;
using Postfach.Mime;
using Postfach.Send;

namespace Postfach.Integration;

[Collection("greenmail")]
public sealed class WriteTests(GreenMailFixture server) : IAsyncLifetime
{
    private const string User = "carol@example.net";
    private Stack _stack = null!;

    public async Task InitializeAsync()
    {
        _stack = new Stack(server.Account(User));
        await server.SeedOnceAsync(User);
    }

    public async Task DisposeAsync() => await _stack.DisposeAsync();

    private async Task<string> AppendAsync(string subject)
    {
        using var client = await server.RawClientAsync(User);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Dave", "dave@example.org"));
        message.To.Add(new MailboxAddress(null, User));
        message.Subject = subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.org");
        message.Body = new TextPart("plain") { Text = "Body of " + subject };
        var uid = await client.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None));
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        return new MessageRef("private", "INBOX", client.Inbox.UidValidity, uid!.Value.Id).ToString();
    }

    [Fact]
    public async Task Updates_flags_and_keywords()
    {
        var id = await AppendAsync("flag-me " + Guid.NewGuid());
        var result = await _stack.Mail.UpdateFlagsAsync([id], ["read", "flagged", "$Todo"], null, default);
        Assert.Equal(1, result.Updated);

        var message = await _stack.Mail.GetMessageAsync(id, BodyFormat.Text, 100, default);
        Assert.DoesNotContain("unread", message.Flags);
        Assert.Contains("flagged", message.Flags);
        Assert.Contains("$Todo", message.Flags);

        await _stack.Mail.UpdateFlagsAsync([id], null, ["read", "flagged"], default);
        message = await _stack.Mail.GetMessageAsync(id, BodyFormat.Text, 100, default);
        Assert.Contains("unread", message.Flags);
        Assert.DoesNotContain("flagged", message.Flags);

        await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.UpdateFlagsAsync([id], ["\\Deleted"], null, default));
        await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.UpdateFlagsAsync([id], ["deleted"], null, default));
        await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.UpdateFlagsAsync(Enumerable.Repeat(id, 101).Select((x, i) => x + i).ToArray(), ["read"], null, default));
    }

    [Theory]
    [InlineData(ImapCapabilities.None)]
    [InlineData(ImapCapabilities.Move)]
    public async Task Archives_and_returns_new_ids(ImapCapabilities disabled)
    {
        await using var stack = new Stack(server.Account(User), disabled: disabled);
        var subject = "archive-me " + Guid.NewGuid();
        var id = await AppendAsync(subject);
        var result = await stack.Mail.MoveAsync([id], "archive", default);
        var newId = result.Moved![id];
        Assert.NotNull(newId);
        Assert.StartsWith("private:Archive:", newId);

        var moved = await stack.Mail.GetMessageAsync(newId!, BodyFormat.Text, 100, default);
        Assert.Equal(subject, moved.Subject);
        Assert.Equal("archive", moved.FolderRole);
        var gone = await Assert.ThrowsAsync<PostfachException>(() => stack.Mail.GetMessageAsync(id, BodyFormat.Text, 100, default));
        Assert.Equal(ErrorCodes.NotFound, gone.Code);
    }

    [Fact]
    public async Task Move_without_move_or_uidplus_is_refused()
    {
        await using var stack = new Stack(server.Account(User), disabled: ImapCapabilities.Move | ImapCapabilities.UidPlus);
        var id = await AppendAsync("no-expunge " + Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<PostfachException>(() => stack.Mail.MoveAsync([id], "archive", default));
        Assert.Equal(ErrorCodes.Unsupported, ex.Code);
        // Still where it was.
        Assert.NotNull(await stack.Mail.GetMessageAsync(id, BodyFormat.Text, 100, default));
    }

    [Fact]
    public async Task Delete_moves_to_trash_and_only_expunges_in_trash()
    {
        var keep = await AppendAsync("keep-me " + Guid.NewGuid());
        await _stack.Mail.UpdateFlagsAsync([keep], ["read"], null, default);
        // Another client marked this one \Deleted without expunging; Postfach must not expunge it.
        using (var client = await server.RawClientAsync(User))
        {
            await client.Inbox.OpenAsync(FolderAccess.ReadWrite);
            await client.Inbox.StoreAsync(new UniqueId(MessageRef.Parse(keep).Uid), new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true });
        }

        var id = await AppendAsync("delete-me " + Guid.NewGuid());
        var result = await _stack.Mail.DeleteAsync([id], default);
        var inTrash = result.Moved![id]!;
        Assert.StartsWith("private:Trash:", inTrash);
        Assert.NotNull(await _stack.Mail.GetMessageAsync(keep, BodyFormat.Text, 100, default));

        await _stack.Mail.DeleteAsync([inTrash], default);
        var ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Mail.GetMessageAsync(inTrash, BodyFormat.Text, 100, default));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.NotNull(await _stack.Mail.GetMessageAsync(keep, BodyFormat.Text, 100, default));
    }

    [Fact]
    public async Task Draft_review_send_flow()
    {
        var subject = "Einladung " + Guid.NewGuid();
        var draft = await _stack.Drafts.CreateAsync(new DraftRequest
        {
            Account = "private",
            To = ["Bob <bob@example.org>"],
            Subject = subject,
            Body = "Hallo Bob,\n\n**Grillabend** am Samstag?",
        }, default);

        Assert.StartsWith("private:Drafts:", draft.Id);
        Assert.Contains("Grillabend", draft.Preview);
        Assert.Empty(draft.Warnings);
        Assert.True(draft.SendEnabled);

        var stored = await _stack.Mail.GetMessageAsync(draft.Id, BodyFormat.Text, 1000, default);
        Assert.Contains("draft", stored.Flags);
        Assert.DoesNotContain("unread", stored.Flags);

        // A forged or wrong token never sends.
        var wrong = await Assert.ThrowsAsync<PostfachException>(() => _stack.Drafts.SendAsync(draft.Id, "forged.token", "test", default));
        Assert.Equal(ErrorCodes.TokenMismatch, wrong.Code);

        var sent = await _stack.Drafts.SendAsync(draft.Id, draft.ConfirmToken, "test", default);
        Assert.True(sent.Sent);
        Assert.Equal(["bob@example.org"], sent.Recipients);
        Assert.Equal("appended", sent.SentCopy);
        Assert.Single(_stack.SendLog.Sends);

        // Delivered to Bob, in Sent, gone from Drafts.
        using (var bob = await server.RawClientAsync("bob@example.org"))
        {
            await bob.Inbox.OpenAsync(FolderAccess.ReadOnly);
            var hits = await bob.Inbox.SearchAsync(SearchQuery.SubjectContains(subject));
            Assert.Single(hits);
            var delivered = await bob.Inbox.GetMessageAsync(hits[0]);
            Assert.Equal("carol@example.net", delivered.From.Mailboxes.Single().Address);
            Assert.Contains("<strong>Grillabend</strong>", delivered.HtmlBody);
            Assert.EndsWith("@example.net", delivered.MessageId);
        }

        var inSent = await _stack.Mail.SearchAsync(null, "sent", new SearchCriteria { Subject = subject }, 5, null, default);
        Assert.Single(inSent.Messages);
        var inDrafts = await _stack.Mail.SearchAsync(null, "drafts", new SearchCriteria { Subject = subject }, 5, null, default);
        Assert.Empty(inDrafts.Messages);

        // Tokens are single-use.
        var again = await Assert.ThrowsAsync<PostfachException>(() => _stack.Drafts.SendAsync(draft.Id, draft.ConfirmToken, "test", default));
        Assert.Contains(again.Code, new[] { ErrorCodes.NotFound, ErrorCodes.TokenMismatch });
    }

    [Fact]
    public async Task Draft_changed_in_another_client_invalidates_token()
    {
        var draft = await _stack.Drafts.CreateAsync(new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Subject = "Harmless " + Guid.NewGuid(),
            Body = "Harmless text",
        }, default);

        // Simulate an edit (e.g. by a prompt-injected agent or the user's mail client): new content, same id slot.
        var reference = MessageRef.Parse(draft.Id);
        using (var client = await server.RawClientAsync(User))
        {
            var drafts = await client.GetFolderAsync("Drafts");
            await drafts.OpenAsync(FolderAccess.ReadWrite);
            var message = await drafts.GetMessageAsync(new UniqueId(reference.Uid));
            message.To.Add(new MailboxAddress(null, "exfil@evil.example"));
            await drafts.AppendAsync(new AppendRequest(message, MessageFlags.Draft));
        }

        // Updating through Postfach issues a new id and token; the old token is useless for the new draft.
        var updated = await _stack.Drafts.UpdateAsync(draft.Id, new DraftRequest { Account = "private", Body = "Edited text" }, default);
        Assert.NotEqual(draft.Id, updated.Id);
        var ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Drafts.SendAsync(updated.Id, draft.ConfirmToken, "test", default));
        Assert.Equal(ErrorCodes.TokenMismatch, ex.Code);
        Assert.Contains("Edited text", updated.Preview);
        Assert.DoesNotContain(_stack.SendLog.Sends, s => s.Recipients.Contains("exfil@evil.example"));
    }

    [Fact]
    public async Task Reply_draft_threads_and_quotes()
    {
        using (var client = await server.RawClientAsync(User))
        {
            var original = await MimeMessage.LoadAsync(Path.Combine(AppContext.BaseDirectory, "corpus", "thread-1.eml"));
            original.MessageId = "reply-test-" + Guid.NewGuid().ToString("N") + "@example.org";
            original.Subject = "Ausflug " + original.MessageId;
            await client.Inbox.AppendAsync(new AppendRequest(original, MessageFlags.None));
        }

        var found = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = "reply-test-" }, 1, null, default);
        var originalId = found.Messages[0].Id;
        var draft = await _stack.Drafts.CreateAsync(new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Body = "Ja, gerne!",
            InReplyTo = originalId,
        }, default);

        Assert.StartsWith("Re: Ausflug", draft.Subject);
        var mime = await _stack.Mail.GetMimeMessageAsync(MessageRef.Parse(draft.Id), default);
        Assert.StartsWith("reply-test-", mime.InReplyTo);
        Assert.Contains("> Hast du am Samstag", mime.TextBody);
        Assert.Empty(draft.Warnings);

        var forward = await _stack.Drafts.CreateAsync(new DraftRequest
        {
            Account = "private",
            To = ["stranger@evil.example"],
            Body = "fyi",
            ForwardOf = originalId,
        }, default);
        Assert.Contains(forward.Warnings, w => w.Contains("not on it"));
    }

    [Fact]
    public async Task Draft_attachments_from_content_and_mailbox()
    {
        // 1x1 transparent PNG.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        var subject = "Foto " + Guid.NewGuid().ToString("N");
        using (var client = await server.RawClientAsync(User))
        {
            var builder = new BodyBuilder { TextBody = "Hier das Foto." };
            builder.Attachments.Add("pixel.png", png, ContentType.Parse("image/png"));
            var message = new MimeMessage { Subject = subject, Body = builder.ToMessageBody() };
            message.From.Add(new MailboxAddress("Dave", "dave@example.org"));
            message.To.Add(new MailboxAddress(null, User));
            await client.Inbox.AppendAsync(new AppendRequest(message, MessageFlags.None));
        }

        var found = await _stack.Mail.SearchAsync(null, null, new SearchCriteria { Subject = subject }, 1, null, default);
        var source = await _stack.Mail.GetMessageAsync(found.Messages[0].Id, BodyFormat.Text, 1000, default);
        var part = source.Attachments.Single(a => a.Filename == "pixel.png").Part;

        var image = await _stack.Mail.GetAttachmentAsync(source.Id, part, 1000, default);
        Assert.True(image.ImageIncluded);
        Assert.Equal(png, image.ImageData);

        var draft = await _stack.Drafts.CreateAsync(new DraftRequest
        {
            Account = "private",
            To = ["friend@elsewhere.example"],
            Subject = "Unterlagen",
            Body = "Anbei.",
            Attachments =
            [
                new DraftAttachment { Filename = "notiz.txt", ContentBase64 = Convert.ToBase64String("Hallo"u8.ToArray()) },
                new DraftAttachment { MessageId = source.Id, Part = part },
            ],
        }, default);
        Assert.Equal(["notiz.txt", "pixel.png"], draft.Attachments.Select(a => a.Filename));
        Assert.Equal(png.Length, draft.Attachments[1].Size);
        Assert.Contains(draft.Warnings, w => w.Contains("pixel.png"));

        // A new body keeps the attachments; remove_attachments drops one.
        var updated = await _stack.Drafts.UpdateAsync(draft.Id, new DraftRequest { Account = "private", Body = "Anbei, korrigiert." }, default);
        Assert.Equal(["notiz.txt", "pixel.png"], updated.Attachments.Select(a => a.Filename));
        var trimmed = await _stack.Drafts.UpdateAsync(updated.Id, new DraftRequest { Account = "private", RemoveAttachments = ["notiz.txt"] }, default);
        Assert.Equal(["pixel.png"], trimmed.Attachments.Select(a => a.Filename));

        var stored = await _stack.Mail.GetMimeMessageAsync(MessageRef.Parse(trimmed.Id), default);
        Assert.Contains("Anbei, korrigiert.", stored.TextBody);
        var storedPart = Assert.IsType<MimePart>(stored.Attachments.Single());
        using var bytes = new MemoryStream();
        storedPart.Content!.DecodeTo(bytes);
        Assert.Equal(png, bytes.ToArray());
    }

    [Fact]
    public async Task Send_switch_off_blocks_sending()
    {
        await using var stack = new Stack(server.Account(User), new SendOptions { AllowSend = false });
        var draft = await stack.Drafts.CreateAsync(new DraftRequest { Account = "private", To = ["bob@example.org"], Subject = "nope", Body = "x" }, default);
        Assert.False(draft.SendEnabled);
        var ex = await Assert.ThrowsAsync<PostfachException>(() => stack.Drafts.SendAsync(draft.Id, draft.ConfirmToken, "test", default));
        Assert.Equal(ErrorCodes.SendDisabled, ex.Code);
    }

    [Fact]
    public async Task Allowlist_and_daily_limit()
    {
        await using var stack = new Stack(server.Account(User), new SendOptions { AllowSend = true, Allowlist = ["*@family.example"], MaxSendsPerDay = 30 });
        var draft = await stack.Drafts.CreateAsync(new DraftRequest { Account = "private", To = ["bob@example.org"], Subject = "x", Body = "x" }, default);
        var ex = await Assert.ThrowsAsync<PostfachException>(() => stack.Drafts.SendAsync(draft.Id, draft.ConfirmToken, "test", default));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        await using var limited = new Stack(server.Account(User), new SendOptions { AllowSend = true, MaxSendsPerDay = 1 });
        limited.SendLog.Sends.Add(new SendAuditEntry("private", "<x@y>", ["bob@example.org"], DateTimeOffset.UtcNow, "test"));
        var second = await limited.Drafts.CreateAsync(new DraftRequest { Account = "private", To = ["bob@example.org"], Subject = "x", Body = "x" }, default);
        ex = await Assert.ThrowsAsync<PostfachException>(() => limited.Drafts.SendAsync(second.Id, second.ConfirmToken, "test", default));
        Assert.Equal(ErrorCodes.RateLimited, ex.Code);
    }

    [Fact]
    public async Task Only_drafts_can_be_sent()
    {
        var id = await AppendAsync("not a draft " + Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<PostfachException>(() => _stack.Drafts.SendAsync(id, "x.y", "test", default));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }
}
