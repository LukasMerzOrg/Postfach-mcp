using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Postfach.Imap;
using Postfach.Send;
using Postfach.Server.Configuration;
using Postfach.Server.OAuth;
using Postfach.Server.Security;
using Postfach.Server.Storage;

namespace Postfach.Server.Setup;

/// <summary>
/// The /setup page: add, test, edit and remove mailboxes, and connect OAuth providers (Google, Microsoft or any
/// OAuth 2 provider) in the browser. Only the signed-in owner can use it; every form carries an antiforgery token.
/// </summary>
public static class SetupEndpoints
{
    private const string ConnectCookie = "postfach_connect";

    private sealed record Pending(string? Password, string? ClientSecret);

    private sealed record ConnectState(string AccountId, string Verifier, string Nonce);

    public static void MapPostfachSetup(this WebApplication app)
    {
        var setup = app.MapGroup("/setup").RequireRateLimiting("oauth");
        setup.MapGet("", DashboardAsync);
        setup.MapGet("/accounts/new", NewAsync);
        setup.MapGet("/accounts/{id}", EditAsync);
        setup.MapPost("/accounts", SubmitAsync);
        setup.MapPost("/accounts/{id}/test", TestAsync);
        setup.MapPost("/accounts/{id}/delete", DeleteAsync);
        setup.MapGet("/accounts/{id}/connect", ConnectAsync);
        setup.MapGet("/oauth/callback", CallbackAsync);
    }

    private static async Task<(SetupContext? Context, IResult? Redirect)> OwnerAsync(HttpContext http, IServiceProvider services)
    {
        OAuthEndpoints.NoCache(http);
        var settings = services.GetRequiredService<OAuthSettings>();
        if (!settings.LocalLoginEnabled && !settings.OidcEnabled)
        {
            return (null, Results.Content(OAuthPages.Error(
                "The setup page needs an owner login: set POSTFACH_OWNER_USERNAME and POSTFACH_OWNER_PASSWORD_HASH (or OIDC) and restart."), "text/html", statusCode: 503));
        }

        var owner = await http.AuthenticateAsync(OAuthEndpoints.OwnerScheme);
        if (!owner.Succeeded || owner.Principal?.Identity?.Name is not { } user)
        {
            var returnUrl = http.Request.Method == "GET" ? http.Request.Path + http.Request.QueryString : "/setup";
            return (null, Results.Redirect("/login?return_url=" + Uri.EscapeDataString(returnUrl)));
        }

        var tokens = services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        var postfach = services.GetRequiredService<PostfachSettings>();
        return (new SetupContext(user, tokens.FormFieldName, tokens.RequestToken!, services.GetService<SecretProtector>() is not null,
            postfach.PublicUrl, postfach.Send.AllowSend), null);
    }

    private static IResult Html(string body, int status = 200) => Results.Content(body, "text/html", statusCode: status);

    private static async Task<IResult> DashboardAsync(HttpContext http, IServiceProvider services, IAccountRegistry registry, AccountCatalog catalog, string? saved, string? removed)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        var message = saved is not null ? $"Saved {saved}." : removed is not null ? $"Removed {removed}." : null;
        return Html(SetupPages.Dashboard(c, registry.Accounts, catalog.Broken, message, null));
    }

    private static async Task<IResult> NewAsync(HttpContext http, IServiceProvider services, IAccountRegistry registry, MailDiscovery discovery, string? email, CancellationToken ct)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Results.Redirect("/setup");
        }

        email = email.Trim();
        DiscoveredSettings found;
        try
        {
            found = await discovery.DiscoverAsync(email, ct);
        }
        catch (ArgumentException ex)
        {
            return Html(SetupPages.Dashboard(c, registry.Accounts, new Dictionary<string, string>(), null, ex.Message), 400);
        }

        var form = AccountForm.FromDiscovery(email, found, registry.Accounts.Select(a => a.Id));
        return Html(SetupPages.Editor(c, form, null, null, null, found.Source));
    }

    private static async Task<IResult> EditAsync(HttpContext http, IServiceProvider services, AccountStore store, string id)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        var dto = store.Get(id);
        return dto is null
            ? Html(OAuthPages.Error("This account is not managed on the setup page."), 404)
            : Html(SetupPages.Editor(c, AccountForm.FromDto(dto), null, null, null, null));
    }

    private static async Task<IResult> SubmitAsync(HttpContext http, IServiceProvider services, IAntiforgery antiforgery, AccountStore store,
        AccountCatalog catalog, IAccountRegistry registry, AccountTester tester, IDataProtectionProvider dataProtection, IAuditLog audit, CancellationToken ct)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        if (!await OAuthEndpoints.IsValidFormAsync(http, antiforgery))
        {
            return Html(OAuthPages.Error("The form expired. Open the setup page again."), 400);
        }

        var protector = services.GetService<SecretProtector>();
        var request = await http.Request.ReadFormAsync(ct);
        var form = AccountForm.FromRequest(request);
        var action = request["action"].ToString();
        var pendingProtector = dataProtection.CreateProtector("postfach.setup.pending").ToTimeLimitedDataProtector();

        // Secrets typed earlier in this edit session (e.g. before a failed test) travel encrypted, never as plain HTML.
        if (request["pending"].FirstOrDefault() is { Length: > 0 } pendingValue)
        {
            try
            {
                var pending = JsonSerializer.Deserialize<Pending>(pendingProtector.Unprotect(pendingValue));
                form.NewPassword ??= pending?.Password;
                form.NewClientSecret ??= pending?.ClientSecret;
            }
            catch (CryptographicException)
            {
            }
        }

        string? NewPending() => form.NewPassword is null && form.NewClientSecret is null
            ? null
            : pendingProtector.Protect(JsonSerializer.Serialize(new Pending(form.NewPassword, form.NewClientSecret)), TimeSpan.FromMinutes(30));

        IResult Again(string? error, AccountTestResult? test, int status = 200)
        {
            var existing = form.IsNew ? null : store.Get(form.Id);
            form.HasStoredPassword = existing?.Secret?.PasswordEnc is not null;
            form.HasStoredClientSecret = existing?.Secret?.Oauth2?.ClientSecretEnc is not null;
            return Html(SetupPages.Editor(c, form, NewPending(), error, test, null), status);
        }

        if (protector is null)
        {
            return Again("POSTFACH_SECRET_KEY is not set; accounts cannot be stored.", null, 400);
        }

        if (string.IsNullOrWhiteSpace(form.Id) || form.Id.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')))
        {
            return Again("The account id may only contain letters, digits, '.', '_' and '-'.", null, 400);
        }

        var existingDto = form.IsNew ? null : store.Get(form.Id);
        if (form.IsNew && registry.Accounts.Any(a => string.Equals(a.Id, form.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return Again($"An account with the id '{form.Id}' already exists.", null, 400);
        }

        if (!form.IsNew && existingDto is null)
        {
            return Html(OAuthPages.Error("This account is not managed on the setup page."), 404);
        }

        AccountDto dto;
        MailAccount account;
        try
        {
            dto = form.ToDto(existingDto, protector);
            account = AccountConfigLoader.BuildAccount(dto, protector, _ => null, [], source: "setup");
        }
        catch (InvalidOperationException ex)
        {
            return Again(ex.Message, null, 400);
        }

        var isOAuth = account.Secret.Kind == SecretKind.OAuth2;
        if (isOAuth && (account.Secret.AuthorizationEndpoint is null || account.Secret.ClientId is null))
        {
            return Again("OAuth needs a client ID and an authorization endpoint (pick a provider preset or fill both in).", null, 400);
        }

        AccountTestResult? test = null;
        var connected = isOAuth && await services.GetRequiredService<IRefreshTokenStore>().GetAsync(account.Id, ct) is not null;
        if (action == "test" || (action == "save" && (!isOAuth || connected)))
        {
            if (isOAuth && !connected)
            {
                return Again("Save the account first, then connect it with the provider; the test runs after connecting.", null);
            }

            test = await tester.TestAsync(account, ct);
            if (action == "test" || !test.Ok)
            {
                return Again(test.Ok ? null : "The test failed, so nothing was saved. Fix the settings, or use 'Save anyway'.", test);
            }
        }

        store.Save(dto);
        catalog.Reload();
        await audit.RecordAsync("setup_account_saved", account.Id, c.User, $"imap={account.Imap.Host}:{account.Imap.Port} auth={account.Secret.Kind}", ct);
        return isOAuth && !connected
            ? Results.Redirect($"/setup/accounts/{Uri.EscapeDataString(account.Id)}/connect")
            : Results.Redirect("/setup?saved=" + Uri.EscapeDataString(account.Id));
    }

    private static async Task<IResult> TestAsync(HttpContext http, IServiceProvider services, IAntiforgery antiforgery, IAccountRegistry registry, AccountTester tester, string id, CancellationToken ct)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        if (!await OAuthEndpoints.IsValidFormAsync(http, antiforgery))
        {
            return Html(OAuthPages.Error("The form expired. Open the setup page again."), 400);
        }

        MailAccount account;
        try
        {
            account = registry.Get(id);
        }
        catch (PostfachException)
        {
            return Html(OAuthPages.Error("Unknown account."), 404);
        }

        return Html(SetupPages.TestPage(c, id, await tester.TestAsync(account, ct), null));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, IServiceProvider services, IAntiforgery antiforgery, AccountStore store, AccountCatalog catalog,
        OAuthTokenSource tokens, IAuditLog audit, string id, CancellationToken ct)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        if (!await OAuthEndpoints.IsValidFormAsync(http, antiforgery))
        {
            return Html(OAuthPages.Error("The form expired. Open the setup page again."), 400);
        }

        store.Delete(id);
        tokens.Invalidate(id);
        catalog.Reload();
        await audit.RecordAsync("setup_account_removed", id, c.User, "removed from Postfach (mailbox untouched)", ct);
        return Results.Redirect("/setup?removed=" + Uri.EscapeDataString(id));
    }

    private static async Task<IResult> ConnectAsync(HttpContext http, IServiceProvider services, IAccountRegistry registry, IDataProtectionProvider dataProtection,
        PostfachSettings settings, string id)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        if (settings.PublicUrl is null)
        {
            return Html(OAuthPages.Error("Set POSTFACH_PUBLIC_URL first: the provider redirects back to it."), 400);
        }

        MailAccount account;
        try
        {
            account = registry.Get(id);
        }
        catch (PostfachException)
        {
            return Html(OAuthPages.Error("Unknown account."), 404);
        }

        var secret = account.Secret;
        if (account.Source != "setup" || secret.Kind != SecretKind.OAuth2 || secret.AuthorizationEndpoint is null || secret.ClientId is null)
        {
            return Html(OAuthPages.Error("This account is not an OAuth account managed on the setup page."), 400);
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(24));
        var protector = dataProtection.CreateProtector("postfach.setup.connect").ToTimeLimitedDataProtector();
        http.Response.Cookies.Append(ConnectCookie, protector.Protect(JsonSerializer.Serialize(new ConnectState(account.Id, verifier, nonce)), TimeSpan.FromMinutes(15)),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = settings.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                SameSite = SameSiteMode.Lax,
                Path = "/setup",
                MaxAge = TimeSpan.FromMinutes(15),
            });

        var query = new List<(string, string)>
        {
            ("client_id", secret.ClientId),
            ("response_type", "code"),
            ("redirect_uri", settings.PublicUrl + "/setup/oauth/callback"),
            ("scope", secret.Scope ?? string.Empty),
            ("state", nonce),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("login_hint", account.Username),
            ("prompt", "consent"),
        };
        if (secret.AuthorizationEndpoint.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            // Google only returns a refresh token for offline access.
            query.Add(("access_type", "offline"));
        }

        var separator = secret.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        return Results.Redirect(secret.AuthorizationEndpoint + separator + string.Join('&', query.Select(q => $"{q.Item1}={Uri.EscapeDataString(q.Item2)}")));
    }

    private static async Task<IResult> CallbackAsync(HttpContext http, IServiceProvider services, IAccountRegistry registry, IDataProtectionProvider dataProtection,
        IHttpClientFactory httpClients, IRefreshTokenStore refreshTokens, OAuthTokenSource tokens, MailService mail, AccountTester tester, PostfachSettings settings,
        IAuditLog audit, ILoggerFactory loggers, string? code, string? state, string? error, string? error_description, CancellationToken ct)
    {
        var (c, redirect) = await OwnerAsync(http, services);
        if (c is null) return redirect!;
        var cookie = http.Request.Cookies[ConnectCookie];
        http.Response.Cookies.Delete(ConnectCookie, new CookieOptions { Path = "/setup" });
        ConnectState? connect = null;
        try
        {
            connect = cookie is null ? null : JsonSerializer.Deserialize<ConnectState>(dataProtection.CreateProtector("postfach.setup.connect").ToTimeLimitedDataProtector().Unprotect(cookie));
        }
        catch (CryptographicException)
        {
        }

        if (connect is null || state is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(connect.Nonce)))
        {
            return Html(OAuthPages.Error("The sign-in with the provider expired or did not start here. Start again from the setup page."), 400);
        }

        if (error is not null)
        {
            return Html(OAuthPages.Error($"The provider refused: {error} {error_description}"), 400);
        }

        MailAccount account;
        try
        {
            account = registry.Get(connect.AccountId);
        }
        catch (PostfachException)
        {
            return Html(OAuthPages.Error("The account no longer exists."), 404);
        }

        var secret = account.Secret;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code ?? string.Empty,
            ["redirect_uri"] = settings.PublicUrl + "/setup/oauth/callback",
            ["client_id"] = secret.ClientId!,
            ["code_verifier"] = connect.Verifier,
        };
        if (!string.IsNullOrEmpty(secret.ClientSecret))
        {
            form["client_secret"] = secret.ClientSecret;
        }

        string? refresh;
        try
        {
            using var response = await httpClients.CreateClient().PostAsync(secret.TokenEndpoint, new FormUrlEncodedContent(form), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                var reason = json.RootElement.TryGetProperty("error_description", out var d) ? d.GetString()
                    : json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : $"HTTP {(int)response.StatusCode}";
                return Html(OAuthPages.Error($"The provider did not issue a token: {reason}"), 400);
            }

            refresh = json.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            loggers.CreateLogger("Postfach.Setup").LogWarning("Token exchange for {Account} failed: {Error}", account.Id, ex.GetType().Name);
            return Html(OAuthPages.Error("Could not reach the provider's token endpoint."), 502);
        }

        if (string.IsNullOrEmpty(refresh))
        {
            return Html(OAuthPages.Error("The provider returned no refresh token. Google: remove Postfach's earlier access at myaccount.google.com/permissions and connect again. Microsoft: make sure the scope includes offline_access."), 400);
        }

        await refreshTokens.SaveAsync(account.Id, refresh, ct);
        tokens.Invalidate(account.Id);
        await mail.InvalidateAccountAsync(account.Id);
        await audit.RecordAsync("setup_account_connected", account.Id, c.User, "oauth refresh token stored", ct);
        return Html(SetupPages.TestPage(c, account.Id, await tester.TestAsync(account, ct), "Connected. Postfach stored the provider's refresh token (encrypted)."));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
