using System.Text;
using Postfach.Imap;
using Postfach.Server.Configuration;
using Postfach.Server.OAuth;
using static Postfach.Server.OAuth.OAuthPages;

namespace Postfach.Server.Setup;

internal sealed record SetupContext(string User, string AfField, string AfToken, bool CanSave, string? PublicUrl, bool SendEnabled);

internal static class SetupPages
{
    private static string Af(SetupContext c) => $"""<input type="hidden" name="{E(c.AfField)}" value="{E(c.AfToken)}">""";

    private static string Header(SetupContext c) => $"""
        <div style="display:flex;justify-content:space-between;align-items:center;gap:8px">
        <h1 style="margin:0"><a href="/setup" style="color:inherit;text-decoration:none">Postfach setup</a></h1>
        <form class="inline" method="post" action="/logout">{Af(c)}<span class="muted">{E(c.User)}</span> <button type="submit">Sign out</button></form>
        </div>
        """;

    public static string Dashboard(SetupContext c, IReadOnlyList<MailAccount> accounts, IReadOnlyDictionary<string, string> broken, string? message, string? error)
    {
        var sb = new StringBuilder(Header(c));
        if (message is not null) sb.Append($"<p class=\"ok\">{E(message)}</p>");
        if (error is not null) sb.Append($"<p class=\"error\">{E(error)}</p>");
        if (!c.CanSave)
        {
            sb.Append("<p class=\"error\">POSTFACH_SECRET_KEY is not set, so accounts cannot be stored here. Set it (generate-key) and restart.</p>");
        }

        sb.Append("<div class=\"box\">");
        sb.Append(c.PublicUrl is null
            ? "<p class=\"warn\">POSTFACH_PUBLIC_URL is not set: claude.ai connectors and 'Connect with Google/Microsoft' are unavailable.</p>"
            : $"<p>MCP endpoint for Claude: <code>{E(c.PublicUrl)}/mcp</code></p>");
        sb.Append($"<p class=\"muted\">Sending is {(c.SendEnabled ? "<strong>enabled</strong>" : "disabled")} on this server (POSTFACH_ALLOW_SEND).</p></div>");

        sb.Append("<h2 style=\"font-size:1.1rem\">Mail accounts</h2>");
        if (accounts.Count == 0 && broken.Count == 0)
        {
            sb.Append("<p class=\"muted\">No accounts yet.</p>");
        }
        else
        {
            sb.Append("<table><tr><th>Account</th><th>Server</th><th>Sign-in</th><th></th></tr>");
            foreach (var a in accounts)
            {
                var auth = a.Secret.Kind == SecretKind.OAuth2 ? "OAuth" : "password";
                var actions = new StringBuilder();
                actions.Append($"""<form class="inline" method="post" action="/setup/accounts/{Uri.EscapeDataString(a.Id)}/test">{Af(c)}<button type="submit">Test</button></form> """);
                if (a.Source == "setup")
                {
                    actions.Append($"""<a href="/setup/accounts/{Uri.EscapeDataString(a.Id)}"><button type="button">Edit</button></a> """);
                    if (a.Secret.Kind == SecretKind.OAuth2)
                    {
                        actions.Append($"""<a href="/setup/accounts/{Uri.EscapeDataString(a.Id)}/connect"><button type="button">Connect</button></a> """);
                    }

                    actions.Append($"""<form class="inline" method="post" action="/setup/accounts/{Uri.EscapeDataString(a.Id)}/delete">{Af(c)}<button type="submit">Remove</button></form>""");
                }
                else
                {
                    actions.Append("<span class=\"muted\">in postfach.yaml</span>");
                }

                sb.Append($"<tr><td><strong>{E(a.Id)}</strong><br><span class=\"muted\">{E(a.Label)} {E(a.Username)}</span></td>"
                    + $"<td>{E(a.Imap.Host)}:{a.Imap.Port}<br><span class=\"muted\">{(a.Smtp is null ? "no SMTP" : $"SMTP {E(a.Smtp.Host)}:{a.Smtp.Port}")}</span></td>"
                    + $"<td>{auth}</td><td>{actions}</td></tr>");
            }

            foreach (var (id, reason) in broken)
            {
                sb.Append($"<tr><td><strong>{E(id)}</strong></td><td colspan=\"2\" class=\"error\">{E(reason)}</td><td>"
                    + $"""<form class="inline" method="post" action="/setup/accounts/{Uri.EscapeDataString(id)}/delete">{Af(c)}<button type="submit">Remove</button></form></td></tr>""");
            }

            sb.Append("</table>");
        }

        sb.Append($"""
            <form method="get" action="/setup/accounts/new" class="box">
            <label>Add an account — email address<input type="email" name="email" required placeholder="you@example.com"></label>
            <div class="row"><button class="primary" type="submit">Find settings</button></div>
            <p class="muted">Postfach looks up the IMAP/SMTP servers (provider presets, autoconfig, Thunderbird ISPDB, DNS). You can change everything on the next page.</p>
            </form>
            """);
        return Layout("Setup · Postfach", sb.ToString(), wide: true);
    }

    public static string Editor(SetupContext c, AccountForm f, string? pending, string? error, AccountTestResult? test, string? discoverySource)
    {
        var sb = new StringBuilder(Header(c));
        sb.Append($"<h2 style=\"font-size:1.1rem\">{(f.IsNew ? "Add account" : $"Edit {E(f.Id)}")}</h2>");
        if (discoverySource is not null)
        {
            var label = discoverySource switch
            {
                "preset" => "built-in provider settings",
                "autoconfig" => "the domain's autoconfig file",
                "ispdb" => "the Thunderbird ISPDB",
                "mx" => "the domain's mail server (MX)",
                "srv" => "DNS SRV records",
                _ => "a guess",
            };
            sb.Append($"<p class=\"muted\">Settings from {label}.</p>");
        }

        if (f.Note is not null) sb.Append($"<p class=\"warn\">{E(f.Note)}</p>");
        if (error is not null) sb.Append($"<p class=\"error\">{E(error)}</p>");
        if (test is not null) sb.Append(TestResult(test));

        string Opt(string value, string current, string label) => $"<option value=\"{value}\"{(value == current ? " selected" : string.Empty)}>{label}</option>";
        string Tls(string name, string current) => $"<select name=\"{name}\">{Opt("implicit", current, "TLS")}{Opt("starttls", current, "STARTTLS")}{Opt("none", current, "None (local bridge only)")}</select>";
        string Check(bool on) => on ? " checked" : string.Empty;
        var presetOptions = new StringBuilder(Opt(string.Empty, f.Provider ?? string.Empty, "Other / generic IMAP"));
        foreach (var p in AccountConfigLoader.Presets.Values)
        {
            presetOptions.Append(Opt(p.Key, f.Provider ?? string.Empty, E(p.Name)));
        }

        sb.Append($"""
            <form method="post" action="/setup/accounts">
            {Af(c)}
            <input type="hidden" name="is_new" value="{(f.IsNew ? "1" : "0")}">
            {(pending is null ? string.Empty : $"<input type=\"hidden\" name=\"pending\" value=\"{E(pending)}\">")}
            <div class="grid">
            <label>Email address<input type="email" name="email" value="{E(f.Email)}" required></label>
            <label>Account id <span class="muted">(used by Claude)</span><input type="text" name="id" value="{E(f.Id)}" pattern="[A-Za-z0-9._-]+" required{(f.IsNew ? string.Empty : " readonly")}></label>
            <label>Label<input type="text" name="label" value="{E(f.Label)}" placeholder="Privat"></label>
            <label>Provider<select name="provider">{presetOptions}</select></label>
            </div>

            <fieldset><legend>Incoming (IMAP)</legend><div class="grid">
            <label>Server<input type="text" name="imap_host" value="{E(f.ImapHost)}" required></label>
            <label>Port<input type="number" name="imap_port" value="{f.ImapPort}" min="1" max="65535" required></label>
            <label>Security{Tls("imap_tls", f.ImapTls)}</label>
            <label>Username<input type="text" name="username" value="{E(f.Username)}" autocomplete="off" required></label>
            </div></fieldset>

            <fieldset><legend>Sign-in</legend>
            <label><select name="auth">{Opt("password", f.Auth, "Password or app password")}{Opt("oauth2", f.Auth, "OAuth (Google, Microsoft, other)")}</select></label>
            <label>Password <span class="muted">{(f.HasStoredPassword || pending is not null ? "(leave empty to keep the stored one)" : string.Empty)}</span><input type="password" name="password" autocomplete="new-password"></label>
            <details{(f.Auth == "oauth2" ? " open" : string.Empty)}><summary>OAuth client</summary>
            <p class="muted">Register your own app with the provider (see docs/providers.md) and add the redirect URI <code>{E(c.PublicUrl ?? "https://<public url>")}/setup/oauth/callback</code>. Endpoints and scope are filled in from the provider preset when left empty.</p>
            <div class="grid">
            <label>Client ID<input type="text" name="oauth_client_id" value="{E(f.OAuthClientId)}" autocomplete="off"></label>
            <label>Client secret <span class="muted">{(f.HasStoredClientSecret ? "(stored; empty = keep)" : string.Empty)}</span><input type="password" name="oauth_client_secret" autocomplete="new-password"></label>
            <label>Tenant <span class="muted">(Microsoft)</span><input type="text" name="oauth_tenant" value="{E(f.OAuthTenant)}" placeholder="common"></label>
            <label>Scope<input type="text" name="oauth_scope" value="{E(f.OAuthScope)}"></label>
            <label>Authorization endpoint<input type="text" name="oauth_authorization_endpoint" value="{E(f.OAuthAuthorizationEndpoint)}"></label>
            <label>Token endpoint<input type="text" name="oauth_token_endpoint" value="{E(f.OAuthTokenEndpoint)}"></label>
            </div></details>
            </fieldset>

            <fieldset><legend>Outgoing (SMTP)</legend>
            <label style="display:flex;gap:8px"><input type="checkbox" name="smtp_enabled"{Check(f.SmtpEnabled)}> Configure SMTP (needed only for sending)</label>
            <div class="grid">
            <label>Server<input type="text" name="smtp_host" value="{E(f.SmtpHost)}"></label>
            <label>Port<input type="number" name="smtp_port" value="{f.SmtpPort}" min="1" max="65535"></label>
            <label>Security{Tls("smtp_tls", f.SmtpTls)}</label>
            </div></fieldset>

            <label>From addresses <span class="muted">(one per line, first is the default; "Name &lt;address&gt;")</span><textarea name="identities" rows="2">{E(f.Identities)}</textarea></label>

            <details><summary>Advanced</summary>
            <label style="display:flex;gap:8px"><input type="checkbox" name="insecure_tls"{Check(f.InsecureTls)}> Allow untrusted certificate (own server with self-signed certificate, or Proton Bridge)</label>
            <div class="grid">
            <label>Copy sent mail to Sent<select name="sent_copy">{Opt("auto", f.SentCopy, "Automatic")}{Opt("always", f.SentCopy, "Always")}{Opt("never", f.SentCopy, "Never")}</select></label>
            <label>Max sends per day<input type="number" name="max_sends_per_day" value="{f.MaxSendsPerDay}" min="1"></label>
            </div>
            <p class="muted">Folder names, only if detection picks the wrong folder (see the test result):</p>
            <div class="grid">
            {string.Join("", AccountForm.FolderRolesEditable.Select(r => $"<label>{r}<input type=\"text\" name=\"folder_{r}\" value=\"{E(f.Folders.GetValueOrDefault(r))}\"></label>"))}
            </div>
            </details>

            <div class="row">
            <button type="submit" name="action" value="test">Test connection</button>
            <button class="primary" type="submit" name="action" value="save"{(c.CanSave ? string.Empty : " disabled")}>{(f.Auth == "oauth2" ? "Save and connect" : "Test and save")}</button>
            </div>
            {(test is { Ok: false } ? "<div class=\"row\"><button type=\"submit\" name=\"action\" value=\"save_anyway\">Save anyway</button></div>" : string.Empty)}
            </form>
            """);
        return Layout("Account · Postfach", sb.ToString(), wide: true);
    }

    public static string TestPage(SetupContext c, string accountId, AccountTestResult result, string? message) =>
        Layout("Test · Postfach", Header(c) + $"<h2 style=\"font-size:1.1rem\">Test of {E(accountId)}</h2>"
            + (message is null ? string.Empty : $"<p class=\"ok\">{E(message)}</p>")
            + TestResult(result) + "<p><a href=\"/setup\">Back to setup</a></p>", wide: true);

    public static string TestResult(AccountTestResult result)
    {
        var sb = new StringBuilder("<div class=\"box\">");
        Step(sb, "IMAP", result.Imap);
        if (result.Smtp is not null)
        {
            Step(sb, "SMTP", result.Smtp);
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    private static void Step(StringBuilder sb, string name, TestStep step)
    {
        sb.Append($"<p><strong>{name}:</strong> <span class=\"{(step.Ok ? "ok" : "error")}\">{E(step.Summary)}</span></p>");
        foreach (var detail in step.Details ?? [])
        {
            sb.Append($"<p class=\"muted\">{E(detail)}</p>");
        }

        if (step.Hint is not null)
        {
            sb.Append($"<p class=\"warn\">{E(step.Hint)}</p>");
        }
    }
}
