using System.Net;
using System.Xml.Linq;
using Postfach.Imap;
using Postfach.Server.Configuration;
using Postfach.Server.Security;
using Postfach.Server.Setup;

namespace Postfach.Unit;

public class DiscoveryTests
{
    private const string Config = """
        <?xml version="1.0"?>
        <clientConfig version="1.1">
          <emailProvider id="example.ch">
            <domain>example.ch</domain>
            <displayName>Example Mail</displayName>
            <incomingServer type="pop3"><hostname>pop.example.ch</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
            <incomingServer type="imap"><hostname>imap.example.ch</hostname><port>143</port><socketType>STARTTLS</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication></incomingServer>
            <incomingServer type="imap"><hostname>imap.example.ch</hostname><port>993</port><socketType>SSL</socketType>
              <username>%EMAILLOCALPART%</username><authentication>password-cleartext</authentication></incomingServer>
            <incomingServer type="imap"><hostname>plain.example.ch</hostname><port>143</port><socketType>plain</socketType></incomingServer>
            <outgoingServer type="smtp"><hostname>smtp.example.ch</hostname><port>587</port><socketType>STARTTLS</socketType></outgoingServer>
          </emailProvider>
        </clientConfig>
        """;

    [Fact]
    public void Parses_mozilla_autoconfig_preferring_tls()
    {
        var found = MailDiscovery.ParseMozillaConfig(XDocument.Parse(Config), "lukas@example.ch", "ispdb")!;
        Assert.Equal(new ServerEndpoint("imap.example.ch", 993, TlsMode.Implicit), found.Imap);
        Assert.Equal(new ServerEndpoint("smtp.example.ch", 587, TlsMode.StartTls), found.Smtp);
        Assert.Equal("lukas", found.Username);
        Assert.Equal("Example Mail", found.ProviderName);
        Assert.False(found.RequiresOAuth);
    }

    [Fact]
    public void Oauth_only_providers_and_plain_only_configs()
    {
        var oauth = Config.Replace("password-cleartext", "OAuth2");
        Assert.True(MailDiscovery.ParseMozillaConfig(XDocument.Parse(oauth), "a@example.ch", "ispdb")!.RequiresOAuth);
        var plainOnly = """<clientConfig><emailProvider><incomingServer type="imap"><hostname>x</hostname><port>143</port><socketType>plain</socketType></incomingServer></emailProvider></clientConfig>""";
        Assert.Null(MailDiscovery.ParseMozillaConfig(XDocument.Parse(plainOnly), "a@b.c", "ispdb"));
    }

    [Fact]
    public async Task Known_domains_use_presets_without_network()
    {
        var discovery = new MailDiscovery(new HttpClient(new FailingHandler()), new DnsClient.LookupClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MailDiscovery>.Instance);
        var gmail = await discovery.DiscoverAsync("someone@gmail.com", default);
        Assert.Equal("gmail", gmail.PresetKey);
        Assert.True(gmail.RequiresOAuth);
        var icloud = await discovery.DiscoverAsync("someone@me.com", default);
        Assert.Equal("imap.mail.me.com", icloud.Imap.Host);
        var proton = await discovery.DiscoverAsync("someone@proton.me", default);
        Assert.True(proton.InsecureTls);
        Assert.Contains("Bridge", proton.Note);
        await Assert.ThrowsAsync<ArgumentException>(() => discovery.DiscoverAsync("no-at-sign", default));
    }

    [Fact]
    public async Task Domain_autoconfig_is_used_first()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "autoconfig.example.ch"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Config), RequestMessage = request }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var discovery = new MailDiscovery(new HttpClient(handler), new DnsClient.LookupClient(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MailDiscovery>.Instance);
        var found = await discovery.DiscoverAsync("lukas@Example.CH", default);
        Assert.Equal("autoconfig", found.Source);
        Assert.Equal("imap.example.ch", found.Imap.Host);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in this test");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

public class LocalBridgeTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.0.3", true)]
    [InlineData("protonmail-bridge", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("imap.example.com", false)]
    public void Local_hosts(string host, bool local) => Assert.Equal(local, AccountConfigLoader.IsLocalHost(host));

    [Fact]
    public void Plaintext_only_for_local_bridges()
    {
        AccountDto Dto(string host) => new()
        {
            Id = "bridge", Username = "me@proton.me",
            Imap = new EndpointDto { Host = host, Port = 1143, Tls = "none" },
            Secret = new SecretDto { Password = "pw" },
        };
        Assert.Equal(TlsMode.None, AccountConfigLoader.BuildAccount(Dto("protonmail-bridge"), null, _ => null, []).Imap.Tls);
        Assert.Throws<InvalidOperationException>(() => AccountConfigLoader.BuildAccount(Dto("imap.example.com"), null, _ => null, []));
    }

    [Fact]
    public void Folder_overrides_are_validated()
    {
        var dto = new AccountDto
        {
            Id = "a", Username = "u@x.example", Imap = new EndpointDto { Host = "imap.x.example" }, Secret = new SecretDto { Password = "pw" },
            Folders = new() { ["sent"] = "INBOX.Gesendet", ["trash"] = "INBOX.Müll" },
        };
        var account = AccountConfigLoader.BuildAccount(dto, null, _ => null, []);
        Assert.Equal("INBOX.Gesendet", account.FolderOverrides[FolderRole.Sent]);
        dto.Folders = new() { ["inbox"] = "x" };
        Assert.Throws<InvalidOperationException>(() => AccountConfigLoader.BuildAccount(dto, null, _ => null, []));
    }

    [Theory]
    [InlineData("Posta inviata", FolderRole.Sent)]
    [InlineData("Brouillons", FolderRole.Drafts)]
    [InlineData("Prullenbak", FolderRole.Trash)]
    [InlineData("Correo no deseado", FolderRole.Junk)]
    public void More_languages(string name, FolderRole role) => Assert.Equal(role, FolderRoles.FromName(name));
}

public class AccountFormTests
{
    private static readonly SecretProtector Protector = new(Convert.ToBase64String(new byte[32]));

    [Fact]
    public void Round_trips_and_keeps_stored_secrets()
    {
        var form = new AccountForm
        {
            Id = "privat", Email = "me@example.ch", Username = "me@example.ch", ImapHost = "imap.example.ch", ImapPort = 993,
            SmtpEnabled = true, SmtpHost = "smtp.example.ch", SmtpPort = 465, SmtpTls = "implicit",
            Identities = "Me <me@example.ch>\nverein@example.ch", NewPassword = "app-pw",
            Folders = new() { ["trash"] = "Deleted" },
        };
        var dto = form.ToDto(null, Protector);
        Assert.StartsWith("enc:v1:", dto.Secret!.PasswordEnc);
        Assert.Null(dto.Secret.Password);
        var account = AccountConfigLoader.BuildAccount(dto, Protector, _ => null, [], "setup");
        Assert.Equal("app-pw", account.Secret.Password);
        Assert.Equal(2, account.Identities.Count);
        Assert.Equal("setup", account.Source);

        var edit = AccountForm.FromDto(dto);
        Assert.True(edit.HasStoredPassword);
        edit.Label = "Privat";
        var again = edit.ToDto(dto, Protector);
        Assert.Equal(dto.Secret.PasswordEnc, again.Secret!.PasswordEnc);
        Assert.Equal("Privat", again.Label);
    }

    [Fact]
    public void Rejects_missing_password_and_bad_identities()
    {
        var form = new AccountForm { Id = "a", Email = "a@b.example", ImapHost = "imap.b.example", Identities = "a@b.example" };
        Assert.Throws<InvalidOperationException>(() => form.ToDto(null, Protector));
        form.NewPassword = "x";
        form.Identities = "not an address";
        Assert.Throws<InvalidOperationException>(() => form.ToDto(null, Protector));
    }

    [Fact]
    public void Suggested_ids_are_unique_and_safe()
    {
        Assert.Equal("lukas-example", AccountForm.SuggestId("Lukas@example.ch", []));
        Assert.Equal("lukas-example-2", AccountForm.SuggestId("lukas@example.ch", ["lukas-example"]));
        Assert.Equal("j-rg-m-ller", AccountForm.SuggestId("jürg+müller@x.ch", [])[..11]);
    }
}

public class PresetTests
{
    [Fact]
    public void Every_preset_builds_an_account()
    {
        foreach (var preset in AccountConfigLoader.Presets.Values)
        {
            var dto = new AccountDto
            {
                Id = preset.Key, Provider = preset.Key, Username = "me@example.com",
                Secret = preset.RequiresOAuth
                    ? new SecretDto { Oauth2 = new OAuthDto { ClientId = "cid", RefreshToken = "rt" } }
                    : new SecretDto { Password = "pw" },
            };
            var account = AccountConfigLoader.BuildAccount(dto, null, _ => null, []);
            Assert.Equal(preset.ImapHost, account.Imap.Host);
            if (preset.RequiresOAuth)
            {
                Assert.NotNull(account.Secret.AuthorizationEndpoint);
                Assert.DoesNotContain("{tenant}", account.Secret.TokenEndpoint);
            }
        }
    }
}
