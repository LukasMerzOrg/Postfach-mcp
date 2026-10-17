using System.Net;
using System.Text;
using Postfach.Server.Configuration;

namespace Postfach.Server.OAuth;

/// <summary>Minimal server-rendered login and consent pages. Everything client-supplied is HTML-encoded.</summary>
internal static class OAuthPages
{
    private static readonly Dictionary<string, string> ScopeLabels = new()
    {
        [Scopes.Read] = "Read mail: search, read messages, threads and attachments",
        [Scopes.Organize] = "Organize: mark read/unread, flag, move and archive",
        [Scopes.Draft] = "Write drafts into your Drafts folder (nothing is sent)",
        [Scopes.Send] = "Send drafts you have approved",
        [Scopes.Delete] = "Move messages to Trash",
    };

    private const string Style = """
        <style>
        :root{color-scheme:light dark;--fg:#1d1d1f;--muted:#666;--bg:#fafafa;--card:#fff;--line:#ddd;--accent:#2f5bd3;--warn:#a15c00}
        @media (prefers-color-scheme:dark){:root{--fg:#eee;--muted:#aaa;--bg:#141414;--card:#1e1e1e;--line:#333;--accent:#7aa2ff;--warn:#f0b35a}}
        body{font:16px/1.5 system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0;padding:24px 16px}
        main{max-width:440px;margin:0 auto;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:24px}
        h1{font-size:1.3rem;margin:0 0 12px} p{margin:8px 0} .muted{color:var(--muted);font-size:.9rem}
        label{display:block;margin:10px 0} input[type=text],input[type=password]{width:100%;box-sizing:border-box;padding:10px;border:1px solid var(--line);border-radius:8px;background:transparent;color:inherit;font:inherit}
        .scope{display:flex;gap:10px;align-items:flex-start;padding:8px 0;border-top:1px solid var(--line)} .scope input{margin-top:5px}
        .warn{color:var(--warn);font-size:.9rem} .error{color:#d33;font-weight:600}
        .row{display:flex;gap:8px;margin-top:16px} button{flex:1;padding:10px;border-radius:8px;border:1px solid var(--line);font:inherit;cursor:pointer;background:transparent;color:inherit}
        button.primary{background:var(--accent);border-color:var(--accent);color:#fff} code{word-break:break-all}
        </style>
        """;

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Layout(string title, string body) =>
        $"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="referrer" content="no-referrer"><title>{E(title)}</title>{Style}</head><body><main>{body}</main></body></html>""";

    public static string Login(string returnUrl, string antiforgeryField, string antiforgeryToken, bool local, bool oidc, string? error)
    {
        var sb = new StringBuilder("<h1>Sign in to Postfach</h1>");
        if (error is not null)
        {
            sb.Append($"<p class=\"error\">{E(error)}</p>");
        }

        if (local)
        {
            sb.Append($"""
                <form method="post" action="/oauth/login">
                <input type="hidden" name="{E(antiforgeryField)}" value="{E(antiforgeryToken)}">
                <input type="hidden" name="return_url" value="{E(returnUrl)}">
                <label>Username<input type="text" name="username" autocomplete="username" required autofocus></label>
                <label>Password<input type="password" name="password" autocomplete="current-password" required></label>
                <div class="row"><button class="primary" type="submit">Sign in</button></div>
                </form>
                """);
        }

        if (oidc)
        {
            sb.Append($"""<div class="row"><a style="flex:1" href="/oauth/login/oidc?return_url={Uri.EscapeDataString(returnUrl)}"><button type="button" style="width:100%">Sign in with single sign-on</button></a></div>""");
        }

        return Layout("Sign in · Postfach", sb.ToString());
    }

    public static string Consent(AuthorizeRequest request, string clientName, string user, string antiforgeryField, string antiforgeryToken, bool sendEnabled)
    {
        var redirectHost = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var u) ? u.Authority : request.RedirectUri;
        var sb = new StringBuilder($"""
            <h1>Allow access to your mail?</h1>
            <p><strong>{E(clientName)}</strong> wants to use Postfach as <strong>{E(user)}</strong>.</p>
            <p class="muted">After approval you will be sent to <code>{E(redirectHost)}</code>.</p>
            <form method="post" action="/oauth/authorize">
            <input type="hidden" name="{E(antiforgeryField)}" value="{E(antiforgeryToken)}">
            """);
        foreach (var (key, value) in request.ToForm())
        {
            sb.Append($"""<input type="hidden" name="{E(key)}" value="{E(value)}">""");
        }

        foreach (var scope in request.Scopes)
        {
            // Send and delete are opt-in even when the client asks for them.
            var isChecked = scope is not (Scopes.Send or Scopes.Delete) ? " checked" : string.Empty;
            var note = scope == Scopes.Send && !sendEnabled ? "<br><span class=\"warn\">Sending is switched off on this server; this permission has no effect until the operator enables it.</span>" : string.Empty;
            sb.Append($"""<label class="scope"><input type="checkbox" name="grant" value="{E(scope)}"{isChecked}><span><code>{E(scope)}</code><br>{E(ScopeLabels.GetValueOrDefault(scope, scope))}{note}</span></label>""");
        }

        sb.Append("""
            <div class="row"><button type="submit" name="decision" value="deny">Deny</button><button class="primary" type="submit" name="decision" value="allow">Allow</button></div>
            </form>
            <p class="muted">Email content is written by strangers. Only grant what you need; you can start read-only.</p>
            """);
        return Layout("Authorize · Postfach", sb.ToString());
    }

    public static string Error(string message) =>
        Layout("Error · Postfach", $"<h1>Authorization failed</h1><p class=\"error\">{E(message)}</p><p class=\"muted\">Close this window and try connecting again.</p>");
}
