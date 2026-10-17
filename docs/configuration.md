# Configuration

Secrets and switches come from environment variables. Mail accounts are added on the **`/setup` page** (stored
encrypted in the database, changes apply immediately) or in `postfach.yaml` (see
[`postfach.example.yaml`](../postfach.example.yaml); read-only in the UI, applied at startup). Both can be mixed.

## The setup page

`/setup` needs the owner login (`POSTFACH_OWNER_USERNAME` + `POSTFACH_OWNER_PASSWORD_HASH`, or OIDC) and
`POSTFACH_SECRET_KEY`. Enter an email address and Postfach looks up the servers in this order: built-in presets
for well-known providers, the domain's Mozilla autoconfig file, the Thunderbird ISPDB, the provider behind the
domain's MX record (Google Workspace, Microsoft 365, iCloud, Fastmail, …), RFC 6186 SRV records, and finally probed
guesses (`imap.<domain>`, `mail.<domain>`). Everything stays editable.

*Test connection* signs in to IMAP (and SMTP, without sending), lists the folders with their detected roles and
explains failures: unknown host, closed port, TLS/certificate problems, missing STARTTLS, rejected login (with
the app-password hint). *Test and save* only stores working settings; *Save anyway* is offered after a failed test.

For OAuth accounts (Gmail, Microsoft, or any OAuth 2 provider with endpoints you enter) the page stores the client
ID/secret, then *Connect* runs the provider's sign-in in your browser and stores the refresh token encrypted.
Register the redirect URI `<POSTFACH_PUBLIC_URL>/setup/oauth/callback` with the provider.

Removing an account only removes it from Postfach; the mailbox is untouched.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `POSTFACH_SECRET_KEY` | — | 32 random bytes, base64. Encrypts secrets at rest (AES-256-GCM) and signs confirm tokens. Without it, `*_enc` secrets are unavailable and confirm tokens do not survive a restart. |
| `POSTFACH_PUBLIC_URL` | — | Public base URL, e.g. `https://postfach.example.com`. Required for OAuth (it is the issuer; the resource is `<url>/mcp`); its origin is allowed by the Origin check. |
| `POSTFACH_OWNER_USERNAME` | — | Owner login for the OAuth sign-in page. |
| `POSTFACH_OWNER_PASSWORD_HASH` | — | Argon2id hash from `hash-password`. |
| `POSTFACH_OIDC_AUTHORITY` | — | Optional: sign in via an OpenID Connect provider instead (e.g. `https://auth.example.com/realms/home`). |
| `POSTFACH_OIDC_CLIENT_ID` / `POSTFACH_OIDC_CLIENT_SECRET` | — | Client at that provider; redirect URI `<public url>/oauth/oidc/callback`. |
| `POSTFACH_OIDC_ALLOWED_EMAILS` / `POSTFACH_OIDC_ALLOWED_SUBJECTS` | — | Who may sign in via OIDC. Empty = nobody (fail closed). |
| `POSTFACH_OAUTH_REDIRECT_ALLOWLIST` | claude.ai, claude.com callbacks and loopback | Redirect URIs clients may register (`*` wildcard; loopback matches any port). Replaces the default list. |
| `POSTFACH_OAUTH_ACCESS_TOKEN_MINUTES` | `60` | Access token lifetime. |
| `POSTFACH_OAUTH_REFRESH_TOKEN_DAYS` | `30` | Refresh token lifetime (renewed on every rotation). |
| `POSTFACH_API_TOKEN` | — | Bearer token for MCP clients (min. 16 chars). Only its SHA-256 is kept in memory. |
| `POSTFACH_API_TOKEN_SCOPES` | `mail.read mail.organize mail.draft` | Scopes of that token. Add `mail.send` / `mail.delete` explicitly. |
| `POSTFACH_ALLOW_SEND` | `false` | Server-side send switch. Overrides any granted scope. |
| `POSTFACH_SEND_ALLOWLIST` | — | Comma-separated recipient patterns, e.g. `*@family.ch,boss@work.ch`. |
| `POSTFACH_MAX_RECIPIENTS` | `20` | Per message (To + Cc + Bcc). |
| `POSTFACH_MAX_SENDS_PER_DAY` | `30` | Per account, rolling 24 h. `max_sends_per_day` in the account overrides it. |
| `POSTFACH_ALLOWED_ORIGINS` | — | Extra allowed browser origins (`https://claude.ai` and `https://claude.com` are always allowed). |
| `POSTFACH_TRUSTED_PROXIES` | loopback + `10/8`, `172.16/12`, `192.168/16` | CIDRs of reverse proxies whose `X-Forwarded-For`/`-Proto` are honoured (one hop). |
| `POSTFACH_RATE_LIMIT_PER_MINUTE` | `120` | MCP requests per token (or IP) per minute. |
| `POSTFACH_DATA_DIR` | `/data` | SQLite database and default config location. |
| `POSTFACH_CONFIG` | `$POSTFACH_DATA_DIR/postfach.yaml` | Account configuration file. |
| `POSTFACH_INSECURE_TLS` | `false` | Disable IMAP/SMTP certificate validation for all accounts (homelab only; logs a warning on every connection). |

## OAuth (claude.ai connectors)

OAuth is on when `POSTFACH_PUBLIC_URL` and a login method (owner password or OIDC) are set. Static API tokens keep
working alongside it.

| Endpoint | Purpose |
|---|---|
| `/.well-known/oauth-protected-resource` | RFC 9728 metadata; the 401 from `/mcp` points here |
| `/.well-known/oauth-authorization-server` | RFC 8414 metadata |
| `POST /oauth/register` | RFC 7591 dynamic client registration (redirect allowlist, max 200 clients) |
| `/oauth/authorize` | Login and consent pages; PKCE S256 required |
| `POST /oauth/token` | `authorization_code` and `refresh_token` grants |
| `POST /oauth/revoke` | RFC 7009 revocation (revokes the whole grant) |

On the consent page `mail.send` and `mail.delete` are unchecked even when the client asks for them. Tokens are
opaque and stored as SHA-256 hashes. `revoke-oauth` signs out every client.

## Accounts

```yaml
accounts:
  - id: private              # appears in message ids; no ':' or spaces
    label: Privat
    provider: fastmail       # optional preset for hosts/ports (gmail, m365, icloud, fastmail, infomaniak)
    username: me@example.com
    imap: { host: imap.example.com, port: 993, tls: implicit }   # or tls: starttls (port 143)
    smtp: { host: smtp.example.com, port: 587, tls: starttls }   # omit for read/draft-only accounts
    secret: { password_env: PRIVATE_PASSWORD }
    identities:
      - { name: Me, email: me@example.com }
      - { name: Me (Verein), email: me@verein.example }
    sent_copy: auto          # append to Sent after sending, unless the provider does it (Gmail, M365)
    # folders: { trash: Deleted }   # only when detection picks the wrong folder
```

Provider presets: `gmail`, `m365`, `outlook`, `icloud`, `fastmail`, `yahoo`, `aol`, `gmx`, `webde`, `mailboxorg`,
`posteo`, `infomaniak`, `bluewin`, `hostpoint`, `zoho`, `protonbridge`.

- With `starttls`, a server that does not advertise STARTTLS is refused (no downgrade).
- `tls: none` exists only for local bridges (Proton Mail Bridge, DavMail, Hydroxide): the host must be `localhost`,
  a private IP or a single-label container name. Postfach never sends a password unencrypted over the internet.
- `folders: { sent: "INBOX.Gesendet", trash: "Deleted" }` overrides folder detection for unusual servers.
- Every secret field accepts `<name>`, `<name>_env` or `<name>_enc`.
- `identities` are the only allowed From addresses. Without them, the username is used if it is an address.

### Encrypting a secret

```sh
printf '%s' 'app-password' | docker run -i --rm -e POSTFACH_SECRET_KEY ghcr.io/lukasmerzorg/postfach-mcp encrypt-secret
# enc:v1:...
```

### Additional API tokens

```sh
docker run --rm ghcr.io/lukasmerzorg/postfach-mcp generate-token
# token:  pf_...      (give this to the client)
# sha256: 4f2a...     (put this in postfach.yaml)
```

```yaml
api_tokens:
  - name: claude-readonly
    sha256: 4f2a...
    scopes: [mail.read]
```

## CLI

| Command | Purpose |
|---|---|
| `generate-key` | Print a new `POSTFACH_SECRET_KEY`. |
| `generate-token` | Print a new API token and its SHA-256. |
| `hash-token <token>` | SHA-256 of an existing token. |
| `hash-password` | Argon2id hash of stdin for `POSTFACH_OWNER_PASSWORD_HASH`. |
| `revoke-oauth` | Revoke all OAuth tokens (uses `POSTFACH_DATA_DIR`). |
| `encrypt-secret` | Encrypt stdin with `POSTFACH_SECRET_KEY`. |

## Endpoints

- `POST /mcp` — Streamable HTTP MCP endpoint (stateless), bearer auth (OAuth access token or API token).
- `GET /healthz` — liveness.
- `GET /readyz` — database reachable and at least one account configured.
- `GET /setup` — account setup (owner login).
