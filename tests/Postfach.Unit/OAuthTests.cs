using Postfach.Server.OAuth;

namespace Postfach.Unit;

public class OAuthUnitTests
{
    [Fact]
    public void Argon2id_round_trip()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("correct horse battery stapl", hash));
        Assert.False(PasswordHasher.Verify("x", "not-a-hash"));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse battery staple"));
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://claude.com/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:6274/oauth/callback", true)]
    [InlineData("http://127.0.0.1:33418/callback", true)]
    [InlineData("https://claude.ai/api/mcp/auth_callback/../evil", false)]
    [InlineData("https://claude.ai.evil.example/api/mcp/auth_callback", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback#frag", false)]
    [InlineData("https://user@claude.ai/api/mcp/auth_callback", false)]
    [InlineData("http://evil.example/callback", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    public void Redirect_allowlist(string uri, bool allowed)
    {
        var settings = new OAuthSettings { Issuer = "https://postfach.example" };
        Assert.Equal(allowed, settings.IsRedirectAllowed(uri));
    }

    [Fact]
    public void Custom_allowlist_replaces_defaults()
    {
        var settings = new OAuthSettings { Issuer = "https://p.example", RedirectAllowlist = ["https://app.example/cb/*"] };
        Assert.True(settings.IsRedirectAllowed("https://app.example/cb/1"));
        Assert.False(settings.IsRedirectAllowed("https://claude.ai/api/mcp/auth_callback"));
    }

    [Fact]
    public void Pkce_s256()
    {
        // RFC 7636 appendix B.
        Assert.True(OAuthEndpoints.PkceMatches("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(OAuthEndpoints.PkceMatches("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXX", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
        Assert.False(OAuthEndpoints.PkceMatches("short", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [Theory]
    [InlineData("/oauth/authorize?client_id=x", "/oauth/authorize?client_id=x")]
    [InlineData("https://evil.example/", "/oauth/authorize")]
    [InlineData("//evil.example/oauth/authorize?", "/oauth/authorize")]
    [InlineData(null, "/oauth/authorize")]
    public void Return_urls_stay_local(string? value, string expected) =>
        Assert.Equal(expected, OAuthEndpoints.SafeReturnUrl(value));

    [Fact]
    public void Enabled_needs_issuer_and_a_login_method()
    {
        Assert.False(OAuthSettings.FromEnvironment(_ => null, null).Enabled);
        Assert.False(OAuthSettings.FromEnvironment(_ => null, "https://p.example").Enabled);
        var env = new Dictionary<string, string> { ["POSTFACH_OWNER_USERNAME"] = "me", ["POSTFACH_OWNER_PASSWORD_HASH"] = "$argon2id$..." };
        var settings = OAuthSettings.FromEnvironment(k => env.GetValueOrDefault(k), "https://p.example/");
        Assert.True(settings.Enabled);
        Assert.Equal("https://p.example/mcp", settings.Resource);
    }
}
