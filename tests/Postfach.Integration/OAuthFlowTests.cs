using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Postfach.Server.OAuth;

namespace Postfach.Integration;

/// <summary>The built-in OAuth 2.1 authorization server, driven like an MCP client (claude.ai / Claude Code) would.</summary>
[Collection("greenmail")]
public sealed partial class OAuthFlowTests : IAsyncLifetime
{
    private const string Owner = "lukas";
    private const string Password = "correct horse battery staple";
    private const string Callback = "http://127.0.0.1:33418/callback";
    private const string Issuer = "http://localhost";

    private readonly GreenMailFixture _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "postfach-oauth-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public OAuthFlowTests(GreenMailFixture server) => _server = server;

    private static readonly string[] Env =
    [
        "POSTFACH_IT_PASSWORD", "POSTFACH_CONFIG", "POSTFACH_DATA_DIR", "POSTFACH_API_TOKEN", "POSTFACH_API_TOKEN_SCOPES",
        "POSTFACH_ALLOW_SEND", "POSTFACH_PUBLIC_URL", "POSTFACH_OWNER_USERNAME", "POSTFACH_OWNER_PASSWORD_HASH",
    ];

    public async Task InitializeAsync()
    {
        await _server.SeedOnceAsync("alice@example.com");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "postfach.yaml"), $"""
            accounts:
              - id: private
                username: alice@example.com
                imap: {"{"} host: {_server.Host}, port: {_server.ImapsPort}, tls: implicit {"}"}
                secret: {"{"} password_env: POSTFACH_IT_PASSWORD {"}"}
                insecure_tls: true
            """);
        Environment.SetEnvironmentVariable("POSTFACH_IT_PASSWORD", "secret");
        Environment.SetEnvironmentVariable("POSTFACH_CONFIG", Path.Combine(_dir, "postfach.yaml"));
        Environment.SetEnvironmentVariable("POSTFACH_DATA_DIR", _dir);
        Environment.SetEnvironmentVariable("POSTFACH_API_TOKEN", null);
        Environment.SetEnvironmentVariable("POSTFACH_PUBLIC_URL", Issuer);
        Environment.SetEnvironmentVariable("POSTFACH_OWNER_USERNAME", Owner);
        Environment.SetEnvironmentVariable("POSTFACH_OWNER_PASSWORD_HASH", PasswordHasher.Hash(Password));
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        foreach (var name in Env)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Unauthenticated_mcp_points_to_resource_metadata()
    {
        using var response = await PostMcpAsync(null, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource\"", challenge);

        var prm = await _client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource");
        Assert.Equal("http://localhost/mcp", prm.GetProperty("resource").GetString());
        Assert.Equal(Issuer, prm.GetProperty("authorization_servers")[0].GetString());

        var meta = await _client.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        Assert.Equal(Issuer, meta.GetProperty("issuer").GetString());
        Assert.Equal(["S256"], meta.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("http://localhost/oauth/register", meta.GetProperty("registration_endpoint").GetString());
    }

    [Fact]
    public async Task Registration_enforces_redirect_allowlist()
    {
        using var bad = await _client.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { "https://evil.example/cb" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var claude = await _client.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" }, client_name = "Claude" });
        Assert.Equal(HttpStatusCode.Created, claude.StatusCode);
        var body = await claude.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("pfc_", body.GetProperty("client_id").GetString());
        Assert.False(body.TryGetProperty("client_secret", out _));
    }

    [Fact]
    public async Task Full_flow_with_scoped_tokens_rotation_and_replay_detection()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();

        // Unauthenticated owner: the authorize endpoint shows the login form.
        var authorizeUrl = $"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=xyz&resource={Uri.EscapeDataString(Issuer + "/mcp")}"
            + $"&scope={Uri.EscapeDataString("mail.read mail.draft mail.send")}";
        var loginPage = await _client.GetStringAsync(authorizeUrl);
        Assert.Contains("Sign in to Postfach", loginPage);

        var wrong = await PostFormAsync("/oauth/login", Fields(loginPage), ("username", Owner), ("password", "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var retryPage = await wrong.Content.ReadAsStringAsync();
        var login = await PostFormAsync("/oauth/login", Fields(retryPage), ("username", Owner), ("password", Password));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        // Consent: send is offered but unchecked by default.
        var consentPage = await _client.GetStringAsync(login.Headers.Location!.OriginalString);
        Assert.Contains("Allow access to your mail?", consentPage);
        Assert.Matches(@"value=""mail\.read"" checked", consentPage);
        Assert.Matches(@"value=""mail\.send"">", consentPage);

        var consent = await PostFormAsync("/oauth/authorize", Fields(consentPage), ("grant", "mail.read"), ("grant", "mail.draft"), ("decision", "allow"));
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var redirect = consent.Headers.Location!;
        Assert.StartsWith(Callback, redirect.OriginalString);
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.Equal("xyz", query["state"]);
        Assert.Equal(Issuer, query["iss"]);
        var code = query["code"]!;

        // Token exchange with PKCE.
        var tokens = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Callback), ("client_id", clientId), ("code_verifier", verifier));
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        var access = tokens.Body.GetProperty("access_token").GetString()!;
        var refresh = tokens.Body.GetProperty("refresh_token").GetString()!;
        Assert.Equal("mail.read mail.draft", tokens.Body.GetProperty("scope").GetString());

        // Codes are single-use.
        var reuse = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Callback), ("client_id", clientId), ("code_verifier", verifier));
        Assert.Equal("invalid_grant", reuse.Body.GetProperty("error").GetString());

        // The token works on /mcp with exactly the granted scopes.
        var accounts = await CallToolAsync(access, "list_accounts", new { });
        Assert.False(accounts.IsError);
        Assert.True(accounts.Payload[0].GetProperty("capabilities").GetProperty("draft").GetBoolean());
        Assert.False(accounts.Payload[0].GetProperty("capabilities").GetProperty("organize").GetBoolean());
        var send = await CallToolAsync(access, "send_draft", new { id = "private:Drafts:1:1", confirm_token = "x.y" });
        Assert.True(send.IsError);
        Assert.Equal("forbidden", send.Payload.GetProperty("error").GetString());

        // Refresh rotates; scopes cannot be widened.
        var widen = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId), ("scope", "mail.read mail.delete"));
        Assert.Equal("invalid_scope", widen.Body.GetProperty("error").GetString());
        var rotated = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId));
        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        var access2 = rotated.Body.GetProperty("access_token").GetString()!;
        Assert.NotEqual(refresh, rotated.Body.GetProperty("refresh_token").GetString());
        Assert.False((await CallToolAsync(access2, "list_folders", new { })).IsError);

        // Replaying the old refresh token revokes the whole grant.
        var replay = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId));
        Assert.Equal("invalid_grant", replay.Body.GetProperty("error").GetString());
        using var revoked = await PostMcpAsync(access2, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Contains("invalid_token", revoked.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Pkce_resource_and_deny_are_enforced()
    {
        var clientId = await RegisterAsync();
        var (verifier, challenge) = Pkce();
        var baseUrl = $"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(Callback)}&state=s1";

        // No PKCE: error goes back to the (registered) redirect URI.
        using var noPkce = await _client.GetAsync(baseUrl);
        Assert.Equal(HttpStatusCode.Redirect, noPkce.StatusCode);
        Assert.Contains("error=invalid_request", noPkce.Headers.Location!.Query);

        // Unregistered redirect URI: never redirect, show an error page.
        using var foreign = await _client.GetAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:9/other")}&code_challenge={challenge}&code_challenge_method=S256");
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // Wrong audience.
        using var wrongResource = await _client.GetAsync(baseUrl + $"&code_challenge={challenge}&code_challenge_method=S256&resource={Uri.EscapeDataString("https://other.example/mcp")}");
        Assert.Contains("error=invalid_target", wrongResource.Headers.Location!.Query);

        var loginPage = await _client.GetStringAsync(baseUrl + $"&code_challenge={challenge}&code_challenge_method=S256");
        var login = await PostFormAsync("/oauth/login", Fields(loginPage), ("username", Owner), ("password", Password));
        var consentPage = await _client.GetStringAsync(login.Headers.Location!.OriginalString);

        // Consent without the antiforgery token is rejected.
        var forged = await PostFormAsync("/oauth/authorize", Fields(consentPage).Where(f => f.Key != "__af").ToList(), ("decision", "allow"), ("grant", "mail.read"));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);

        var deny = await PostFormAsync("/oauth/authorize", Fields(consentPage), ("decision", "deny"));
        Assert.Contains("error=access_denied", deny.Headers.Location!.Query);

        var allow = await PostFormAsync("/oauth/authorize", Fields(consentPage), ("decision", "allow"), ("grant", "mail.read"));
        var code = System.Web.HttpUtility.ParseQueryString(allow.Headers.Location!.Query)["code"]!;
        var badVerifier = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", clientId), ("code_verifier", new string('a', 50)));
        Assert.Equal("invalid_grant", badVerifier.Body.GetProperty("error").GetString());
    }

    private async Task<string> RegisterAsync()
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new { redirect_uris = new[] { Callback }, client_name = "Integration Test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static List<KeyValuePair<string, string>> Fields(string html) =>
        HiddenInput().Matches(html)
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups["name"].Value), WebUtility.HtmlDecode(m.Groups["value"].Value)))
            .ToList();

    private async Task<HttpResponseMessage> PostFormAsync(string path, List<KeyValuePair<string, string>> fields, params (string Key, string Value)[] extra)
    {
        var all = fields.Concat(extra.Select(e => new KeyValuePair<string, string>(e.Key, e.Value))).ToList();
        return await _client.PostAsync(path, new FormUrlEncodedContent(all));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(params (string Key, string Value)[] form)
    {
        using var response = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<HttpResponseMessage> PostMcpAsync(string? token, string method, object? @params = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    private async Task<(bool IsError, JsonElement Payload)> CallToolAsync(string token, string tool, object arguments)
    {
        using var response = await PostMcpAsync(token, "tools/call", new { name = tool, arguments });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var data = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..] ?? text;
        var result = JsonDocument.Parse(data).RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (isError, JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone());
    }

    [GeneratedRegex(@"<input type=""hidden"" name=""(?<name>[^""]*)"" value=""(?<value>[^""]*)"">")]
    private static partial Regex HiddenInput();
}
