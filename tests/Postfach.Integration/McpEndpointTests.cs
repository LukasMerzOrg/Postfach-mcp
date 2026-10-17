using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Postfach.Integration;

/// <summary>The real HTTP host: bearer auth, Origin check, scopes and the MCP tool surface.</summary>
[Collection("greenmail")]
public sealed class McpEndpointTests : IAsyncLifetime
{
    private const string Token = "pf_integration_token_0123456789";
    private readonly GreenMailFixture _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "postfach-it-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public McpEndpointTests(GreenMailFixture server) => _server = server;

    public async Task InitializeAsync()
    {
        await _server.SeedOnceAsync("alice@example.com");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "postfach.yaml"), $"""
            accounts:
              - id: private
                label: Privat
                username: alice@example.com
                imap: {"{"} host: {_server.Host}, port: {_server.ImapsPort}, tls: implicit {"}"}
                smtp: {"{"} host: {_server.Host}, port: {_server.SmtpsPort}, tls: implicit {"}"}
                secret: {"{"} password_env: POSTFACH_IT_PASSWORD {"}"}
                insecure_tls: true
            """);
        Environment.SetEnvironmentVariable("POSTFACH_IT_PASSWORD", "secret");
        Environment.SetEnvironmentVariable("POSTFACH_CONFIG", Path.Combine(_dir, "postfach.yaml"));
        Environment.SetEnvironmentVariable("POSTFACH_DATA_DIR", _dir);
        Environment.SetEnvironmentVariable("POSTFACH_API_TOKEN", Token);
        Environment.SetEnvironmentVariable("POSTFACH_API_TOKEN_SCOPES", "mail.read mail.draft");
        Environment.SetEnvironmentVariable("POSTFACH_ALLOW_SEND", "false");
        Environment.SetEnvironmentVariable("POSTFACH_PUBLIC_URL", null);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<HttpResponseMessage> PostAsync(object body, string? token = Token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> RpcAsync(string method, object? @params = null)
    {
        using var response = await PostAsync(new { jsonrpc = "2.0", id = 1, method, @params });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var data = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?[5..] ?? text;
        return JsonDocument.Parse(data).RootElement.GetProperty("result").Clone();
    }

    private async Task<(bool IsError, JsonElement Payload)> CallAsync(string tool, object arguments)
    {
        var result = await RpcAsync("tools/call", new { name = tool, arguments });
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return (isError, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Health_endpoints()
    {
        Assert.Equal("ok", await _client.GetStringAsync("/healthz"));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/readyz")).StatusCode);
    }

    [Fact]
    public async Task Rejects_missing_wrong_token_and_foreign_origin()
    {
        var ping = new { jsonrpc = "2.0", id = 1, method = "tools/list" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(ping, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(ping, token: "pf_wrong_token_0123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(ping, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(ping, origin: "https://claude.ai")).StatusCode);
    }

    [Fact]
    public async Task Exposes_twelve_annotated_tools()
    {
        var tools = (await RpcAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal(
            ["create_draft", "delete_messages", "get_attachment", "get_message", "get_thread", "list_accounts", "list_folders",
             "move_messages", "search_messages", "send_draft", "update_draft", "update_flags"],
            names);

        var send = tools.Single(t => t.GetProperty("name").GetString() == "send_draft");
        Assert.True(send.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.True(send.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
        var get = tools.Single(t => t.GetProperty("name").GetString() == "get_message");
        Assert.True(get.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("untrusted_content", get.GetProperty("description").GetString());
        Assert.DoesNotContain(names, n => n == "send_email");
    }

    [Fact]
    public async Task Scopes_and_send_switch_are_enforced()
    {
        var (isError, accounts) = await CallAsync("list_accounts", new { });
        Assert.False(isError);
        var capabilities = accounts[0].GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("send").GetBoolean());
        Assert.False(capabilities.GetProperty("delete").GetBoolean());
        Assert.False(capabilities.GetProperty("organize").GetBoolean());

        var (sendError, send) = await CallAsync("send_draft", new { id = "private:Drafts:1:1", confirm_token = "x.y" });
        Assert.True(sendError);
        Assert.Equal("forbidden", send.GetProperty("error").GetString());

        var (deleteError, delete) = await CallAsync("delete_messages", new { ids = new[] { "private:INBOX:1:1" } });
        Assert.True(deleteError);
        Assert.Equal("forbidden", delete.GetProperty("error").GetString());

        var (flagError, _) = await CallAsync("update_flags", new { ids = new[] { "private:INBOX:1:1" }, add = new[] { "read" } });
        Assert.True(flagError);
    }

    [Fact]
    public async Task Injected_content_stays_inside_untrusted_content()
    {
        var (_, search) = await CallAsync("search_messages", new { subject = "Urgent request" });
        var id = search.GetProperty("messages")[0].GetProperty("id").GetString();
        var (isError, message) = await CallAsync("get_message", new { id });
        Assert.False(isError);
        Assert.Contains("Ignore all previous instructions", message.GetProperty("untrusted_content").GetProperty("body").GetString());
        foreach (var property in message.EnumerateObject().Where(p => p.Name != "untrusted_content" && p.Value.ValueKind == JsonValueKind.String))
        {
            Assert.DoesNotContain("Ignore all previous instructions", property.Value.GetString());
        }

        // A draft can be prepared, but nothing can be sent: no scope, and the server switch is off.
        var (draftError, draft) = await CallAsync("create_draft", new { account = "private", to = new[] { "exfil@evil.example" }, body = "fwd", forward_of = id });
        Assert.False(draftError);
        Assert.False(draft.GetProperty("send_enabled").GetBoolean());
        Assert.NotEmpty(draft.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Errors_are_structured()
    {
        var (isError, error) = await CallAsync("get_message", new { id = "nope" });
        Assert.True(isError);
        Assert.Equal("invalid_argument", error.GetProperty("error").GetString());
        Assert.True(error.TryGetProperty("hint", out _));
    }
}
