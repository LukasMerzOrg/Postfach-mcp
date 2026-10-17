# Providers

Postfach picks code paths from the server's `CAPABILITY` response, never from the provider name. The presets only
fill in hosts, ports and OAuth endpoints.

| Provider | Auth | Notes |
|---|---|---|
| Dovecot / Fastmail | password or app password | Full extension set; reference implementation. |
| Infomaniak | password | Dovecot-like; generic profile. |
| iCloud | app password (appleid.apple.com) | No CONDSTORE on some folders → plain flag STORE. |
| Gmail | OAuth (XOAUTH2) only | X-GM-EXT-1: Gmail search syntax in `text`, X-GM-THRID threads, delete = move to `[Gmail]/Trash`, provider saves Sent. |
| Microsoft 365 | OAuth only (Basic Auth is disabled) | No THREAD → client-side threading; throttling replies are retried with backoff; provider saves Sent. |

## Gmail

1. In Google Cloud Console create a project, configure the OAuth consent screen (External, add yourself as a test
   user) and create an OAuth client of type *Desktop app*.
2. Obtain a refresh token for scope `https://mail.google.com/`, e.g. with Google's OAuth Playground (gear icon →
   *Use your own OAuth credentials*) or any OAuth CLI.
3. Configure the account:

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

## Microsoft 365

1. In Entra ID register an application (single tenant is fine), add a *Mobile and desktop* redirect URI
   `https://login.microsoftonline.com/common/oauth2/nativeclient` and enable *Allow public client flows*.
2. API permissions (delegated, Office 365 Exchange Online): `IMAP.AccessAsUser.All`, `SMTP.Send`, plus
   `offline_access`.
3. Obtain a refresh token with the device-code flow for scope
   `https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access`.
4. SMTP AUTH must be enabled for the mailbox (`Set-CASMailbox -SmtpClientAuthenticationDisabled $false`).

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

## iCloud

Create an app-specific password at appleid.apple.com, then:

```yaml
  - id: icloud
    provider: icloud
    username: me@icloud.com
    secret: { password_env: ICLOUD_APP_PASSWORD }
```
