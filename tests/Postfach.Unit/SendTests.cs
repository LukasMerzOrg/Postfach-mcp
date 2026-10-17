using MimeKit;
using Postfach.Imap;
using Postfach.Send;

namespace Postfach.Unit;

internal static class TestAccounts
{
    public static MailAccount Alice() => new()
    {
        Id = "private",
        Username = "alice@example.com",
        Imap = new ServerEndpoint("imap.example.com", 993, TlsMode.Implicit),
        Smtp = new ServerEndpoint("smtp.example.com", 465, TlsMode.Implicit),
        Secret = new AccountSecret { Kind = SecretKind.Password, Password = "x" },
        Identities = [new MailIdentity("Alice", "alice@example.com"), new MailIdentity("Alice (Verein)", "alice@verein.example")],
    };
}

public class DraftBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Builds_multipart_alternative_with_message_id_on_sender_domain()
    {
        var message = DraftBuilder.Build(TestAccounts.Alice(), new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Subject = "Hallo",
            Body = "**Fett** und <script>alert(1)</script>",
        }, null, false, Now);

        Assert.Equal("alice@example.com", message.From.Mailboxes.Single().Address);
        Assert.EndsWith("@example.com", message.MessageId);
        Assert.Contains("**Fett**", message.TextBody);
        Assert.Contains("<strong>Fett</strong>", message.HtmlBody);
        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
        Assert.IsType<MultipartAlternative>(message.Body);
    }

    [Fact]
    public void Reply_sets_threading_headers_and_quotes()
    {
        var original = Corpus.Load("thread-1.eml");
        var reply = DraftBuilder.Build(TestAccounts.Alice(), new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Body = "Gerne!",
            InReplyTo = "x",
        }, original, false, Now);

        Assert.Equal("Re: Ausflug Samstag", reply.Subject);
        Assert.Equal("thread-root@example.org", reply.InReplyTo);
        Assert.Equal(["thread-root@example.org"], reply.References.ToArray());
        Assert.Contains("> Hast du am Samstag", reply.TextBody);
        Assert.Contains("<blockquote", reply.HtmlBody);
    }

    [Fact]
    public void Forward_keeps_attachments()
    {
        var original = Corpus.Load("nested-multipart.eml");
        var forward = DraftBuilder.Build(TestAccounts.Alice(), new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Body = "FYI",
            ForwardOf = "x",
        }, original, true, Now);

        Assert.Equal("Fwd: Steuererklaerung Unterlagen", forward.Subject);
        Assert.Equal(2, forward.Attachments.Count());
        Assert.Contains("Forwarded message", forward.TextBody);
        Assert.Null(forward.InReplyTo);
    }

    [Fact]
    public void Identity_must_belong_to_account()
    {
        var account = TestAccounts.Alice();
        Assert.Equal("alice@verein.example", DraftBuilder.ResolveIdentity(account, "ALICE@verein.example").Email);
        var ex = Assert.Throws<PostfachException>(() => DraftBuilder.ResolveIdentity(account, "ceo@bank.example"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    [InlineData("bob@")]
    public void Rejects_invalid_recipients(string address)
    {
        Assert.Throws<PostfachException>(() => DraftBuilder.ParseRecipients([address], "to"));
    }

    [Fact]
    public void Content_hash_survives_serialization_round_trip()
    {
        var message = DraftBuilder.Build(TestAccounts.Alice(), new DraftRequest
        {
            Account = "private",
            To = ["bob@example.org"],
            Subject = "Grüsse aus Boniswil",
            Body = "Zeile eins\n\nZeile zwei mit Ümlaut",
            ForwardOf = "x",
        }, Corpus.Load("nested-multipart.eml"), true, Now);

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        stream.Position = 0;
        var reparsed = MimeMessage.Load(stream);
        Assert.Equal(ConfirmTokens.ContentHash(message), ConfirmTokens.ContentHash(reparsed));

        reparsed.To.Add(new MailboxAddress(null, "eve@evil.example"));
        Assert.NotEqual(ConfirmTokens.ContentHash(message), ConfirmTokens.ContentHash(reparsed));
    }
}

public class ConfirmTokenTests
{
    private static readonly MessageRef Draft = new("private", "Drafts", 7, 42);
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero));

    private ConfirmTokens Tokens() => new(new byte[32], _time);

    [Fact]
    public void Valid_token_verifies()
    {
        var (token, expires) = Tokens().Issue(Draft, "abc");
        Assert.Equal(_time.Now.AddMinutes(15), expires);
        Assert.NotEmpty(Tokens().Verify(token, Draft, "abc"));
    }

    [Fact]
    public void Changed_content_is_rejected()
    {
        var (token, _) = Tokens().Issue(Draft, "abc");
        AssertMismatch(() => Tokens().Verify(token, Draft, "abd"));
    }

    [Fact]
    public void Other_draft_is_rejected()
    {
        var (token, _) = Tokens().Issue(Draft, "abc");
        AssertMismatch(() => Tokens().Verify(token, Draft with { Uid = 43 }, "abc"));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var (token, _) = Tokens().Issue(Draft, "abc");
        _time.Now = _time.Now.AddMinutes(16);
        AssertMismatch(() => Tokens().Verify(token, Draft, "abc"));
    }

    [Fact]
    public void Forged_or_foreign_key_tokens_are_rejected()
    {
        var (token, _) = Tokens().Issue(Draft, "abc");
        var other = new ConfirmTokens(Enumerable.Repeat((byte)1, 32).ToArray(), _time);
        AssertMismatch(() => other.Verify(token, Draft, "abc"));
        AssertMismatch(() => Tokens().Verify("garbage", Draft, "abc"));
        AssertMismatch(() => Tokens().Verify(null, Draft, "abc"));
        AssertMismatch(() => Tokens().Verify(token[..^2] + "AA", Draft, "abc"));
    }

    private static void AssertMismatch(Action action) =>
        Assert.Equal(ErrorCodes.TokenMismatch, Assert.Throws<PostfachException>(action).Code);
}

public class SendPolicyTests
{
    [Fact]
    public void Allowlist_wildcards()
    {
        var patterns = new[] { "*@family.ch", "boss@work.ch" };
        Assert.True(SendPolicy.IsAllowed("mami@family.ch", patterns));
        Assert.True(SendPolicy.IsAllowed("BOSS@work.ch", patterns));
        Assert.False(SendPolicy.IsAllowed("colleague@work.ch", patterns));
        Assert.False(SendPolicy.IsAllowed("x@family.ch.evil.example", patterns));
    }

    [Fact]
    public void Recipient_limits()
    {
        var options = new SendOptions { AllowSend = true, MaxRecipients = 2, Allowlist = ["*@family.ch"] };
        Assert.Equal(ErrorCodes.RateLimited, Assert.Throws<PostfachException>(() => SendPolicy.CheckRecipients(["a@family.ch", "b@family.ch", "c@family.ch"], options)).Code);
        Assert.Equal(ErrorCodes.Forbidden, Assert.Throws<PostfachException>(() => SendPolicy.CheckRecipients(["a@other.ch"], options)).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<PostfachException>(() => SendPolicy.CheckRecipients([], options)).Code);
        SendPolicy.CheckRecipients(["a@family.ch"], options);
    }
}

public class ExfiltrationGuardTests
{
    private static MimeMessage Draft(string to, string body)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(MailboxAddress.Parse(to));
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    [Fact]
    public void Internal_recipients_are_not_flagged()
    {
        var body = "> quoted\n" + new string('A', 200);
        Assert.Empty(ExfiltrationGuard.Check(TestAccounts.Alice(), Draft("me@verein.example", body), body, false, null));
    }

    [Fact]
    public void Encoded_strings_to_external_are_flagged()
    {
        var body = "Hi\n" + Convert.ToBase64String(new byte[120]);
        Assert.Contains(ExfiltrationGuard.Check(TestAccounts.Alice(), Draft("x@evil.example", body), body, false, null), w => w.Contains("encoded"));
    }

    [Fact]
    public void Quoting_in_a_new_message_to_external_is_flagged()
    {
        var body = "Here is what Bob said:\n> secret plans";
        Assert.Contains(ExfiltrationGuard.Check(TestAccounts.Alice(), Draft("x@evil.example", body), body, false, null), w => w.Contains("quotes"));
    }

    [Fact]
    public void Forwarding_to_a_new_external_recipient_is_flagged()
    {
        var original = Corpus.Load("thread-1.eml");
        Assert.Contains(ExfiltrationGuard.Check(TestAccounts.Alice(), Draft("x@evil.example", "fyi"), "fyi", true, original), w => w.Contains("not on it"));
        Assert.Empty(ExfiltrationGuard.Check(TestAccounts.Alice(), Draft("bob@example.org", "ok"), "ok", true, original));
    }
}
