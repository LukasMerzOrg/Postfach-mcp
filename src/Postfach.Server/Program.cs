using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Postfach.Imap;
using Postfach.Send;
using Postfach.Server;
using Postfach.Server.Configuration;
using Postfach.Server.OAuth;
using Postfach.Server.Security;
using Postfach.Server.Setup;
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

var oauth = OAuthSettings.FromEnvironment(Environment.GetEnvironmentVariable, settings.PublicUrl);
var secureCookies = settings.PublicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
var databasePath = Path.Combine(settings.DataDirectory, "postfach.db");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(oauth);
builder.Services.AddSingleton(settings.Send);
builder.Services.AddSingleton(TimeProvider.System);
// YAML accounts plus accounts managed on /setup; the catalog rebuilds the registry when the setup page saves.
var registry = new AccountRegistry(config.Accounts);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton<IAccountRegistry>(registry);
builder.Services.AddSingleton(sp => new AccountStore(databasePath, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new AccountCatalog(registry, sp.GetRequiredService<AccountStore>(), config.Accounts, protector, sp.GetRequiredService<ILogger<AccountCatalog>>()));
if (protector is not null)
{
    builder.Services.AddSingleton(protector);
}

builder.Services.AddSingleton(sp => new AccountTester(sp.GetRequiredService<OAuthTokenSource>(), sp.GetRequiredService<ILoggerFactory>(), settings.InsecureTls));
builder.Services.AddSingleton<DnsClient.ILookupClient>(new DnsClient.LookupClient(new DnsClient.LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 1, UseCache = true }));
builder.Services.AddHttpClient<MailDiscovery>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Postfach-autoconfig/1.0");
});
builder.Services.AddSingleton(new ApiTokenStore(apiTokens));
builder.Services.AddSingleton(sp => new SqliteStore(databasePath, protector, sp.GetRequiredService<TimeProvider>()));
if (oauth.Enabled)
{
    builder.Services.AddSingleton(sp => new OAuthStore(databasePath, sp.GetRequiredService<TimeProvider>()));
}

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

// Cookies and antiforgery tokens for the login/consent pages; keys survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("postfach")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.DataDirectory, "keys")));
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "postfach_af";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.FormFieldName = "__af";
});

var authentication = builder.Services
    .AddAuthentication(BearerTokenHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, null)
    .AddCookie(OAuthEndpoints.OwnerScheme, o =>
    {
        o.Cookie.Name = "postfach_owner";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });

if (oauth.OidcEnabled)
{
    authentication.AddOpenIdConnect(OAuthEndpoints.OidcScheme, o =>
    {
        o.Authority = oauth.OidcAuthority;
        o.ClientId = oauth.OidcClientId;
        o.ClientSecret = oauth.OidcClientSecret;
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false;
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.CallbackPath = "/oauth/oidc/callback";
        o.SignInScheme = OAuthEndpoints.OwnerScheme;
        o.CorrelationCookie.Path = "/oauth";
        o.NonceCookie.Path = "/oauth";
        o.Events.OnTokenValidated = context =>
        {
            var email = context.Principal?.FindFirst("email")?.Value;
            var verified = context.Principal?.FindFirst("email_verified")?.Value;
            var subject = context.Principal?.FindFirst("sub")?.Value;
            var emailAllowed = email is not null && verified is not "false"
                && oauth.OidcAllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase);
            var subjectAllowed = subject is not null && oauth.OidcAllowedSubjects.Contains(subject, StringComparer.Ordinal);
            if (!emailAllowed && !subjectAllowed)
            {
                // Fail closed: an empty allowlist admits nobody.
                context.Fail("This account is not allowed to sign in to Postfach.");
                return Task.CompletedTask;
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, email ?? subject!), new Claim("amr", "oidc")], OAuthEndpoints.OwnerScheme);
            context.Principal = new ClaimsPrincipal(identity);
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("mcp", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = settings.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("oauth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
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
app.Services.GetRequiredService<AccountCatalog>().Reload();

foreach (var warning in config.Warnings)
{
    app.Logger.LogWarning("{Warning}", warning);
}

if (oauth.Enabled)
{
    app.Logger.LogInformation("OAuth authorization server enabled at {Issuer} (local login: {Local}, OIDC: {Oidc})", oauth.Issuer, oauth.LocalLoginEnabled, oauth.OidcEnabled);
    if (oauth.OidcEnabled && oauth.OidcAllowedEmails.Count == 0 && oauth.OidcAllowedSubjects.Count == 0)
    {
        app.Logger.LogWarning("OIDC is configured but POSTFACH_OIDC_ALLOWED_EMAILS/SUBJECTS is empty: nobody can sign in via OIDC.");
    }
}
else
{
    app.Logger.LogInformation("OAuth authorization server disabled (needs POSTFACH_PUBLIC_URL plus POSTFACH_OWNER_USERNAME/POSTFACH_OWNER_PASSWORD_HASH or OIDC).");
}

if (apiTokens.Count == 0 && !oauth.Enabled)
{
    app.Logger.LogWarning("No API tokens and no OAuth configured: every MCP request will be rejected.");
}

if (settings.InsecureTls)
{
    app.Logger.LogWarning("POSTFACH_INSECURE_TLS is on: certificate validation is disabled for all IMAP/SMTP connections.");
}

app.Logger.LogInformation("Postfach starting with {Accounts} account(s); sending {Send}", registry.Accounts.Count, settings.Send.AllowSend ? "ENABLED" : "disabled");

var origins = new HashSet<string>(OriginCheckMiddleware.DefaultOrigins.Concat(settings.AllowedOrigins), StringComparer.OrdinalIgnoreCase);
if (settings.PublicUrl is { } publicUrl && Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri))
{
    origins.Add(publicUri.GetLeftPart(UriPartial.Authority));
}

// Behind SWAG/Caddy every request comes from the proxy; take the client IP and scheme from trusted proxies only,
// so per-IP rate limits (login!) and audit entries see the real client.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Clear();
foreach (var cidr in settings.TrustedProxies)
{
    if (System.Net.IPNetwork.TryParse(cidr, out var network))
    {
        forwarded.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(network.BaseAddress, network.PrefixLength));
    }
    else
    {
        app.Logger.LogWarning("Ignoring invalid POSTFACH_TRUSTED_PROXIES entry {Entry}", cidr);
    }
}

app.UseForwardedHeaders(forwarded);

// DNS-rebinding/CSRF protection for the MCP endpoint; the OAuth forms use antiforgery tokens instead.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.UseMiddleware<OriginCheckMiddleware>((IReadOnlySet<string>)origins));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapGet("/readyz", (SqliteStore store, IAccountRegistry accounts) =>
    store.IsHealthy() && accounts.Accounts.Count > 0
        ? Results.Json(new { status = "ready", accounts = accounts.Accounts.Count })
        : Results.Json(new { status = "not_ready", database = store.IsHealthy(), accounts = accounts.Accounts.Count }, statusCode: 503));
app.MapGet("/", () => Results.Redirect("/setup"));

if (oauth.LocalLoginEnabled || oauth.OidcEnabled)
{
    app.MapOwnerLogin(oauth);
}

if (oauth.Enabled)
{
    app.MapPostfachOAuth(oauth);
}

app.MapPostfachSetup();

app.MapMcp("/mcp").RequireAuthorization().RequireRateLimiting("mcp");

app.Run();
return 0;

public partial class Program;
