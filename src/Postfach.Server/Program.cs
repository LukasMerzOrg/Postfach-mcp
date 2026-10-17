using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Postfach.Imap;
using Postfach.Send;
using Postfach.Server;
using Postfach.Server.Configuration;
using Postfach.Server.Security;
using Postfach.Server.Storage;
using Postfach.Server.Tools;

if (args.Length > 0 && Cli.TryRun(args, out var exitCode))
{
    return exitCode;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = false);

var settings = PostfachSettings.FromEnvironment(Environment.GetEnvironmentVariable);
var protector = string.IsNullOrWhiteSpace(settings.SecretKey) ? null : new SecretProtector(settings.SecretKey);
var config = AccountConfigLoader.Load(settings.ConfigPath, protector, Environment.GetEnvironmentVariable);
var apiTokens = config.Tokens.ToList();
if (!string.IsNullOrEmpty(settings.ApiToken))
{
    apiTokens.Add(new ApiToken("env", ApiTokenStore.Hash(settings.ApiToken), settings.ApiTokenScopes));
}

// The confirm-token key must survive restarts so a draft shown before a restart can still be sent;
// without POSTFACH_SECRET_KEY a random per-process key is used.
var confirmKey = protector?.DeriveKey("postfach/confirm-token/v1") ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Send);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAccountRegistry>(new AccountRegistry(config.Accounts));
builder.Services.AddSingleton(new ApiTokenStore(apiTokens));
builder.Services.AddSingleton(sp => new SqliteStore(Path.Combine(settings.DataDirectory, "postfach.db"), protector, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ISendLog>(sp => sp.GetRequiredService<SqliteStore>());
builder.Services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<SqliteStore>());
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<SqliteStore>());
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new OAuthTokenSource(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OAuthTokenSource)),
    sp.GetRequiredService<IRefreshTokenStore>(),
    sp.GetRequiredService<ILogger<OAuthTokenSource>>()));
builder.Services.AddSingleton(sp => new ImapConnector(sp.GetRequiredService<OAuthTokenSource>(), sp.GetRequiredService<ILogger<ImapConnector>>(), settings.InsecureTls));
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton(sp => new ConfirmTokens(confirmKey, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IMailSubmitter>(sp => new SmtpSubmitter(sp.GetRequiredService<OAuthTokenSource>(), sp.GetRequiredService<ILogger<SmtpSubmitter>>(), settings.InsecureTls));
builder.Services.AddSingleton<DraftService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ToolRunner>();

builder.Services
    .AddAuthentication(BearerTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("mcp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "postfach", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" };
        o.ServerInstructions = ServerText.Instructions;
    })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<MailTools>();

var app = builder.Build();

foreach (var warning in config.Warnings)
{
    app.Logger.LogWarning("{Warning}", warning);
}

if (apiTokens.Count == 0)
{
    app.Logger.LogWarning("No API tokens configured: every MCP request will be rejected. Set POSTFACH_API_TOKEN or api_tokens in postfach.yaml.");
}

if (settings.InsecureTls)
{
    app.Logger.LogWarning("POSTFACH_INSECURE_TLS is on: certificate validation is disabled for all IMAP/SMTP connections.");
}

app.Logger.LogInformation("Postfach starting with {Accounts} account(s); sending {Send}", config.Accounts.Count, settings.Send.AllowSend ? "ENABLED" : "disabled");

var origins = new HashSet<string>(OriginCheckMiddleware.DefaultOrigins.Concat(settings.AllowedOrigins), StringComparer.OrdinalIgnoreCase);
if (settings.PublicUrl is { } publicUrl && Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri))
{
    origins.Add(publicUri.GetLeftPart(UriPartial.Authority));
}

app.UseMiddleware<OriginCheckMiddleware>((IReadOnlySet<string>)origins);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", (SqliteStore store, IAccountRegistry accounts) =>
    store.IsHealthy() && accounts.Accounts.Count > 0
        ? Results.Json(new { status = "ready", accounts = accounts.Accounts.Count })
        : Results.Json(new { status = "not_ready", database = store.IsHealthy(), accounts = accounts.Accounts.Count }, statusCode: 503));
app.MapGet("/", () => Results.Text("Postfach MCP server. MCP endpoint: /mcp\n"));

app.MapMcp("/mcp").RequireAuthorization().RequireRateLimiting("mcp");

app.Run();
return 0;

public partial class Program;
