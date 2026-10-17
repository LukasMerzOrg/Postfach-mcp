using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Postfach.Imap;
using Postfach.Server.OAuth;

namespace Postfach.Integration;

/// <summary>The /setup page driven like a browser: add, test, edit, connect and remove accounts at runtime.</summary>
[Collection("greenmail")]
public sealed partial class SetupFlowTests : IAsyncLifetime
{
    private const string Owner = "lukas";
    private const string Password = "correct horse battery staple";
    private const string ApiToken = "pf_setup_test_token_0123456789";

    private readonly GreenMailFixture _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "postfach-setup-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpRequestMessage> _providerRequests = [];
    private readonly List<string> _providerBodies = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static readonly string[] Env =
    [
        "POSTFACH_CONFIG", "POSTFACH_DATA_DIR", "POSTFACH_API_TOKEN", "POSTFACH_API_TOKEN_SCOPES", "POSTFACH_PUBLIC_URL",
        "POSTFACH_OWNER_USERNAME", "POSTFACH_OWNER_PASSWORD_HASH", "POSTFACH_SECRET_KEY", "POSTFACH_ALLOW_SEND",
    ];

    public SetupFlowTests(GreenMailFixture server) => _server = server;

    public async Task InitializeAsync()
    {
        await _server.SeedOnceAsync("alice@example.com");
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("POSTFACH_CONFIG", Path.Combine(_dir, "none.yaml"));
        Environment.SetEnvironmentVariable("POSTFACH_DATA_DIR", _dir);
        Environment.SetEnvironmentVariable("POSTFACH_API_TOKEN", ApiToken);
        Environment.SetEnvironmentVariable("POSTFACH_API_TOKEN_SCOPES", "mail.read");
        Environment.SetEnvironmentVariable("POSTFACH_PUBLIC_URL", "http://localhost");
        Environment.SetEnvironmentVariable("POSTFACH_OWNER_USERNAME", Owner);
        Environment.SetEnvironmentVariable("POSTFACH_OWNER_PASSWORD_HASH", PasswordHasher.Hash(Password));
        Environment.SetEnvironmentVariable("POSTFACH_SECRET_KEY", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.ConfigureHttpClientDefaults(h => h.ConfigurePrimaryHttpMessageHandler(() => new FakeProvider(this)))));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginAsync();
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

    private async Task LoginAsync()
    {
        var redirect = await _client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/login", redirect.Headers.Location!.OriginalString);
        var page = await _client.GetStringAsync(redirect.Headers.Location.OriginalString);
        var login = await PostAsync("/oauth/login", page, ("username", Owner), ("password", Password));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/setup", login.Headers.Location!.OriginalString);
    }

    private (string, string)[] GreenMailFields(string id, string password, int? port = null, string tls = "implicit") =>
    [
        ("is_new", "1"), ("id", id), ("email", "alice@example.com"), ("username", "alice@example.com"), ("label", "Alice"),
        ("imap_host", _server.Host), ("imap_port", (port ?? _server.ImapsPort).ToString()), ("imap_tls", tls),
        ("smtp_enabled", "on"), ("smtp_host", _server.Host), ("smtp_port", _server.SmtpsPort.ToString()), ("smtp_tls", "implicit"),
        ("auth", "password"), ("password", password), ("identities", "Alice <alice@example.com>"), ("insecure_tls", "on"),
    ];

    [Fact]
    public async Task Add_test_save_use_edit_and_remove_an_account_without_restart()
    {
        var setup = await _client.GetStringAsync("/setup");
        Assert.Contains("No accounts yet", setup);

        // Wrong password: diagnosed, nothing saved.
        var wrong = await PostAsync("/setup/accounts", setup, [.. GreenMailFields("alice", "nope"), ("action", "save")]);
        var wrongHtml = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("rejected the login", wrongHtml);
        Assert.Contains("app-specific password", wrongHtml);
        Assert.DoesNotContain("value=\"nope\"", wrongHtml);
        Assert.Contains("Save anyway", wrongHtml);

        // Test only.
        var test = await PostAsync("/setup/accounts", setup, [.. GreenMailFields("alice", "secret"), ("action", "test")]);
        var testHtml = await test.Content.ReadAsStringAsync();
        Assert.Contains("IMAP works.", testHtml);
        Assert.Contains("SMTP works (nothing was sent).", testHtml);
        Assert.DoesNotContain("value=\"secret\"", testHtml);

        // The password typed before the test travels encrypted in "pending"; saving without retyping works.
        var fields = Fields(testHtml).Where(f => f.Key is "pending").Select(f => (f.Key, f.Value)).ToList();
        Assert.Single(fields);
        var save = await PostAsync("/setup/accounts", testHtml,
            [.. GreenMailFields("alice", "").Where(f => f.Item1 != "password"), .. fields, ("action", "save")]);
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Equal("/setup?saved=alice", save.Headers.Location!.OriginalString);

        // Immediately usable over MCP.
        var accounts = await CallToolAsync("list_accounts", new { });
        Assert.Equal("alice", accounts[0].GetProperty("id").GetString());
        var search = await CallToolAsync("search_messages", new { account = "alice", subject = "Abfallkalender" });
        Assert.Equal("Abfallkalender 2027", search.GetProperty("messages")[0].GetProperty("subject").GetString());

        // Edit with an empty password keeps the stored one.
        var edit = await _client.GetStringAsync("/setup/accounts/alice");
        Assert.Contains("leave empty to keep", edit);
        var resave = await PostAsync("/setup/accounts", edit,
            [.. GreenMailFields("alice", "").Where(f => f.Item1 is not ("password" or "is_new" or "label")), ("is_new", "0"), ("label", "Alice privat"), ("action", "save")]);
        Assert.Equal(HttpStatusCode.Redirect, resave.StatusCode);
        var dashboard = await _client.GetStringAsync("/setup");
        Assert.Contains("Alice privat", dashboard);
        Assert.Equal("Alice privat", (await CallToolAsync("list_accounts", new { }))[0].GetProperty("label").GetString());

        // Duplicate ids are refused.
        var duplicate = await PostAsync("/setup/accounts", dashboard, [.. GreenMailFields("alice", "secret"), ("action", "save")]);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        // Test button on the dashboard.
        var retest = await PostAsync("/setup/accounts/alice/test", dashboard);
        Assert.Contains("IMAP works.", await retest.Content.ReadAsStringAsync());

        // Remove.
        var remove = await PostAsync("/setup/accounts/alice/delete", dashboard);
        Assert.Equal("/setup?removed=alice", remove.Headers.Location!.OriginalString);
        var (isError, _) = await CallToolRawAsync("list_folders", new { account = "alice" });
        Assert.True(isError);
    }

    [Fact]
    public async Task Local_bridge_without_tls_and_forms_without_antiforgery()
    {
        var setup = await _client.GetStringAsync("/setup");
        var noToken = await _client.PostAsync("/setup/accounts", new FormUrlEncodedContent(
            GreenMailFields("x", "secret").Append(("action", "save")).Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        // A plaintext IMAP port on a local address is accepted (as for Proton Bridge or DavMail).
        var bridge = await PostAsync("/setup/accounts", setup,
            [.. GreenMailFields("bridge", "secret", _server.ImapPlainPort, "none").Where(f => f.Item1 != "smtp_enabled"), ("action", "save")]);
        Assert.Equal(HttpStatusCode.Redirect, bridge.StatusCode);
        var folders = await CallToolAsync("list_folders", new { account = "bridge" });
        Assert.Contains(folders.EnumerateArray(), f => f.GetProperty("role").GetString() == "inbox");
    }

    [Fact]
    public async Task Discovery_prefills_known_providers()
    {
        var page = await _client.GetStringAsync("/setup/accounts/new?email=someone%40gmail.com");
        Assert.Contains("value=\"imap.gmail.com\"", page);
        Assert.Contains("Google requires OAuth", page);
        Assert.Contains("value=\"oauth2\" selected", page);
        Assert.Contains("http://localhost/setup/oauth/callback", page);
    }

    [Fact]
    public async Task Oauth_connect_round_trip_with_a_provider()
    {
        var setup = await _client.GetStringAsync("/setup");
        var save = await PostAsync("/setup/accounts", setup,
        [
            ("is_new", "1"), ("id", "custom-oauth"), ("email", "alice@example.com"), ("username", "alice@example.com"),
            ("imap_host", _server.Host), ("imap_port", _server.ImapsPort.ToString()), ("imap_tls", "implicit"), ("insecure_tls", "on"),
            ("auth", "oauth2"), ("oauth_client_id", "my-client"), ("oauth_client_secret", "my-secret"),
            ("oauth_authorization_endpoint", "https://provider.example/authorize"), ("oauth_token_endpoint", "https://provider.example/token"),
            ("oauth_scope", "imap offline_access"), ("identities", "alice@example.com"), ("action", "save"),
        ]);
        Assert.Equal("/setup/accounts/custom-oauth/connect", save.Headers.Location!.OriginalString);

        var connect = await _client.GetAsync(save.Headers.Location.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        var authorize = connect.Headers.Location!;
        Assert.Equal("provider.example", authorize.Host);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("my-client", query["client_id"]);
        Assert.Equal("http://localhost/setup/oauth/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("alice@example.com", query["login_hint"]);

        // A forged state is refused.
        var forged = await _client.GetAsync("/setup/oauth/callback?code=x&state=forged");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        connect = await _client.GetAsync(save.Headers.Location.OriginalString);
        query = System.Web.HttpUtility.ParseQueryString(connect.Headers.Location!.Query);

        var callback = await _client.GetAsync($"/setup/oauth/callback?code=the-code&state={Uri.EscapeDataString(query["state"]!)}");
        var html = await callback.Content.ReadAsStringAsync();
        Assert.Contains("Connected.", html);

        var exchange = _providerBodies.Single(b => b.Contains("grant_type=authorization_code"));
        Assert.Contains("code=the-code", exchange);
        Assert.Contains("code_verifier=", exchange);
        Assert.Contains("client_secret=my-secret", exchange);
        var stored = await _factory.Services.GetRequiredService<IRefreshTokenStore>().GetAsync("custom-oauth", default);
        Assert.Equal("provider-refresh-token", stored);
    }

    private async Task<JsonElement> CallToolAsync(string tool, object arguments)
    {
        var (isError, payload) = await CallToolRawAsync(tool, arguments);
        Assert.False(isError, payload.ToString());
        return payload;
    }

    private async Task<(bool IsError, JsonElement Payload)> CallToolRawAsync(string tool, object arguments)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var data = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..] ?? text;
        var result = JsonDocument.Parse(data).RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (isError, JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone());
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string pageWithToken, params (string Key, string Value)[] fields)
    {
        var af = Fields(pageWithToken).First(f => f.Key == "__af");
        var all = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(af).ToList();
        return await _client.PostAsync(path, new FormUrlEncodedContent(all));
    }

    private static List<KeyValuePair<string, string>> Fields(string html) =>
        HiddenInput().Matches(html)
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups["name"].Value), WebUtility.HtmlDecode(m.Groups["value"].Value)))
            .ToList();

    [GeneratedRegex(@"<input type=""hidden"" name=""(?<name>[^""]*)"" value=""(?<value>[^""]*)"">")]
    private static partial Regex HiddenInput();

    /// <summary>Stands in for Google/Microsoft: answers the token endpoint, refuses everything else.</summary>
    private sealed class FakeProvider(SetupFlowTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner._providerRequests)
            {
                owner._providerRequests.Add(request);
            }

            if (request.RequestUri!.Host == "provider.example" && request.RequestUri.AbsolutePath == "/token")
            {
                owner._providerBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"provider-access","refresh_token":"provider-refresh-token","expires_in":3600}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
