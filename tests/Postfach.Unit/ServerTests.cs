using Postfach.Imap;
using Postfach.Server.Configuration;
using Postfach.Server.Security;

namespace Postfach.Unit;

public class SecretProtectorTests
{
    private static readonly string Key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void Round_trip()
    {
        var protector = new SecretProtector(Key);
        var enc = protector.Protect("app-password-äöü");
        Assert.StartsWith("enc:v1:", enc);
        Assert.DoesNotContain("app-password", enc);
        Assert.Equal("app-password-äöü", protector.Unprotect(enc));
        Assert.NotEqual(enc, protector.Protect("app-password-äöü"));
    }

    [Fact]
    public void Wrong_key_or_tampering_fails()
    {
        var enc = new SecretProtector(Key).Protect("secret");
        var other = new SecretProtector(Convert.ToBase64String(new byte[32]));
        Assert.Throws<InvalidOperationException>(() => other.Unprotect(enc));
        var tampered = enc[..^4] + (enc[^4] == 'A' ? "B" : "A") + enc[^3..];
        Assert.Throws<InvalidOperationException>(() => new SecretProtector(Key).Unprotect(tampered));
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        Assert.Throws<InvalidOperationException>(() => new SecretProtector("short"));
        Assert.Throws<InvalidOperationException>(() => new SecretProtector(Convert.ToBase64String(new byte[16])));
    }
}

public class AccountConfigTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void Provider_presets_and_env_secrets()
    {
        var file = new ConfigFile
        {
            Accounts =
            [
                new AccountDto
                {
                    Id = "icloud", Provider = "icloud", Username = "alice@icloud.com",
                    Secret = new SecretDto { PasswordEnv = "ICLOUD_PW" },
                },
                new AccountDto
                {
                    Id = "work", Provider = "m365", Username = "alice@corp.example",
                    Secret = new SecretDto { Oauth2 = new OAuthDto { ClientId = "cid", Tenant = "contoso", RefreshTokenEnv = "M365_RT" } },
                },
            ],
        };

        var config = AccountConfigLoader.Build(file, null, Env(("ICLOUD_PW", "pw"), ("M365_RT", "rt")));
        var icloud = config.Accounts[0];
        Assert.Equal(new ServerEndpoint("imap.mail.me.com", 993, TlsMode.Implicit), icloud.Imap);
        Assert.Equal(new ServerEndpoint("smtp.mail.me.com", 587, TlsMode.StartTls), icloud.Smtp);
        Assert.Equal("pw", icloud.Secret.Password);
        Assert.Equal("alice@icloud.com", icloud.DefaultIdentity.Email);

        var work = config.Accounts[1];
        Assert.Equal(SecretKind.OAuth2, work.Secret.Kind);
        Assert.Equal("https://login.microsoftonline.com/contoso/oauth2/v2.0/token", work.Secret.TokenEndpoint);
        Assert.Equal("rt", work.Secret.RefreshToken);
        Assert.Contains("IMAP.AccessAsUser.All", work.Secret.Scope);
    }

    [Fact]
    public void Encrypted_secrets()
    {
        var protector = new SecretProtector(Convert.ToBase64String(new byte[32]));
        var file = new ConfigFile
        {
            Accounts = [new AccountDto { Id = "a", Username = "u@x.example", Imap = new EndpointDto { Host = "imap.x.example" }, Secret = new SecretDto { PasswordEnc = protector.Protect("pw") } }],
        };
        Assert.Equal("pw", AccountConfigLoader.Build(file, protector, _ => null).Accounts[0].Secret.Password);
        Assert.Throws<InvalidOperationException>(() => AccountConfigLoader.Build(file, null, _ => null));
    }

    [Fact]
    public void Plain_password_warns()
    {
        var file = new ConfigFile
        {
            Accounts = [new AccountDto { Id = "a", Username = "u", Imap = new EndpointDto { Host = "h", Port = 143 }, Secret = new SecretDto { Password = "pw" } }],
        };
        var config = AccountConfigLoader.Build(file, null, _ => null);
        Assert.Contains(config.Warnings, w => w.Contains("plain text"));
        Assert.Equal(TlsMode.StartTls, config.Accounts[0].Imap.Tls);
    }

    [Theory]
    [InlineData("bad:id", "none")]
    [InlineData("ok", "none")]
    [InlineData("ok", "weird")]
    public void Rejects_invalid_accounts(string id, string tls)
    {
        var file = new ConfigFile
        {
            Accounts = [new AccountDto { Id = id, Username = "u", Imap = new EndpointDto { Host = "h", Tls = tls == "none" && id == "bad:id" ? null : tls }, Secret = new SecretDto { Password = "pw" } }],
        };
        Assert.Throws<InvalidOperationException>(() => AccountConfigLoader.Build(file, null, _ => null));
    }

    [Fact]
    public void Api_tokens_validate_scopes()
    {
        var hash = Convert.ToHexString(ApiTokenStore.Hash("pf_test_token_1234567890"));
        var ok = AccountConfigLoader.Build(new ConfigFile { ApiTokens = [new ApiTokenDto { Name = "claude", Sha256 = hash, Scopes = ["mail.read"] }] }, null, _ => null);
        Assert.Equal(["mail.read"], ok.Tokens[0].Scopes);
        Assert.Throws<InvalidOperationException>(() =>
            AccountConfigLoader.Build(new ConfigFile { ApiTokens = [new ApiTokenDto { Name = "x", Sha256 = hash, Scopes = ["mail.everything"] }] }, null, _ => null));
    }
}

public class ApiTokenStoreTests
{
    [Fact]
    public void Matches_by_hash_only()
    {
        var store = new ApiTokenStore([new ApiToken("claude", ApiTokenStore.Hash("pf_correct_token_value"), Scopes.DefaultGrant)]);
        Assert.Equal("claude", store.Match("pf_correct_token_value")?.Name);
        Assert.Null(store.Match("pf_correct_token_valuX"));
        Assert.Null(store.Match("short"));
    }

    [Fact]
    public void Default_grant_excludes_send_and_delete()
    {
        Assert.DoesNotContain(Scopes.Send, Scopes.DefaultGrant);
        Assert.DoesNotContain(Scopes.Delete, Scopes.DefaultGrant);
        var settings = PostfachSettings.FromEnvironment(_ => null);
        Assert.False(settings.Send.AllowSend);
        Assert.Equal(20, settings.Send.MaxRecipients);
        Assert.Equal(30, settings.Send.MaxSendsPerDay);
        Assert.Contains("172.16.0.0/12", settings.TrustedProxies);
        Assert.Equal(["10.1.0.0/16"], PostfachSettings.FromEnvironment(k => k == "POSTFACH_TRUSTED_PROXIES" ? "10.1.0.0/16" : null).TrustedProxies);
    }
}
