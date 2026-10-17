# Providers

Postfach works with any IMAP4rev1 server. It picks code paths from the server's `CAPABILITY` response, never from
the provider name; the presets only fill in hosts, ports and OAuth endpoints. The easiest way to add an account is
the `/setup` page: enter the email address, check the prefilled settings, *Test and save*.

Compatibility notes for less common servers:

- Password login skips mechanisms that cannot work from a container (NTLM, GSSAPI, Kerberos) and falls back from
  CRAM-MD5/SCRAM to PLAIN/LOGIN (over TLS) if the server rejects them.
- Postfach sends IMAP `ID` when offered (some providers refuse `SELECT` without it) and enables `UTF8=ACCEPT`.
- Folder roles come from SPECIAL-USE, then names in German, English, French, Italian, Spanish, Dutch and Portuguese;
  override them per account when a server uses other names. Missing Drafts/Sent folders are created on first use.
- Servers without MOVE and UIDPLUS can be read and drafted to; moving and deleting are refused (no plain EXPUNGE).

| Provider | Auth | Notes |
|---|---|---|
| Dovecot / Fastmail | password or app password | Full extension set; reference implementation. |
| Infomaniak | password | Dovecot-like; generic profile. |
| iCloud | app password (appleid.apple.com) | No CONDSTORE on some folders → plain flag STORE. |
| Gmail | OAuth (XOAUTH2) only | X-GM-EXT-1: Gmail search syntax in `text`, X-GM-THRID threads, delete = move to `[Gmail]/Trash`, provider saves Sent. |
| Microsoft 365 | OAuth only (Basic Auth is disabled) | No THREAD → client-side threading; throttling replies are retried with backoff; provider saves Sent. |

## App passwords

iCloud, Yahoo, AOL, Fastmail, Zoho and Gmail-with-IMAP-password all need an app-specific password instead of your
normal one. GMX and WEB.DE need "POP3/IMAP access" enabled in their web settings first.

## Gmail / Google Workspace

1. In Google Cloud Console create a project and enable nothing else; configure the OAuth consent screen (External;
   add yourself as a test user, or publish it for your Workspace) with the scope `https://mail.google.com/`.
2. Create an OAuth client of type **Web application** with the authorized redirect URI
   `https://postfach.example.com/setup/oauth/callback`.
3. On `/setup`: add the address (the Gmail preset is detected, also for Workspace domains via MX), enter client ID
   and secret, *Save and connect*, sign in with Google.

In an "External / Testing" consent screen Google expires refresh tokens after 7 days; publish the app (no
verification is needed for personal use with your own account) to avoid reconnecting weekly.

Equivalent `postfach.yaml` (refresh token obtained elsewhere):

```yaml
  - id: gmail
    provider: gmail
    username: someone@gmail.com
    secret:
      oauth2:
        client_id: 1234.apps.googleusercontent.com
        client_secret_env: GMAIL_CLIENT_SECRET
        refresh_token_enc: enc:v1:...
```

## Microsoft 365 and Outlook.com

1. In Entra ID (portal.azure.com → App registrations) register an application. Supported account types: your
   organization for Microsoft 365; *personal Microsoft accounts* for Outlook.com/Hotmail (preset `outlook`).
2. Add a **Web** redirect URI `https://postfach.example.com/setup/oauth/callback` and create a client secret.
3. API permissions (delegated): Office 365 Exchange Online `IMAP.AccessAsUser.All` and `SMTP.Send`, plus
   `offline_access`.
4. For Microsoft 365, SMTP AUTH must be enabled for the mailbox (`Set-CASMailbox -SmtpClientAuthenticationDisabled $false`).
5. On `/setup`: add the address (M365 domains are detected via MX), enter client ID, secret and tenant (your tenant
   ID or domain; leave empty for `common`), *Save and connect*, sign in with Microsoft.

```yaml
  - id: work
    provider: m365
    username: me@contoso.com
    secret:
      oauth2:
        tenant: contoso.onmicrosoft.com
        client_id: 00000000-0000-0000-0000-000000000000
        refresh_token_enc: enc:v1:...
```

Rotated refresh tokens are stored encrypted in the SQLite database (requires `POSTFACH_SECRET_KEY`) and take
precedence over the configured one.

## Proton Mail

Proton offers IMAP only through **Proton Mail Bridge**. Run it as a container on the same Docker network (e.g.
`shenxn/protonmail-bridge`), log in once in its CLI, then add the account with the `Proton Mail (via Bridge)`
preset: host = the Bridge container's name, ports 1143/1025, the password the Bridge shows, "Allow untrusted
certificate" on (the Bridge uses a self-signed certificate). If your Bridge only offers plain connections, choose
security *None* — allowed because the host is local.

## Exchange on-premises via DavMail

DavMail exposes Exchange (EWS/O365) as local IMAP/SMTP. Run it next to Postfach and add it like the Bridge (security
*None* on the local network, or STARTTLS if you configured a certificate).

## iCloud

Create an app-specific password at appleid.apple.com, then:

```yaml
  - id: icloud
    provider: icloud
    username: me@icloud.com
    secret: { password_env: ICLOUD_APP_PASSWORD }
```
