# Configuration

Secrets and switches come from environment variables; accounts come from `postfach.yaml`
(see [`postfach.example.yaml`](../postfach.example.yaml)).

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `POSTFACH_SECRET_KEY` | — | 32 random bytes, base64. Encrypts secrets at rest (AES-256-GCM) and signs confirm tokens. Without it, `*_enc` secrets are unavailable and confirm tokens do not survive a restart. |
| `POSTFACH_API_TOKEN` | — | Bearer token for MCP clients (min. 16 chars). Only its SHA-256 is kept in memory. |
| `POSTFACH_API_TOKEN_SCOPES` | `mail.read mail.organize mail.draft` | Scopes of that token. Add `mail.send` / `mail.delete` explicitly. |
| `POSTFACH_ALLOW_SEND` | `false` | Server-side send switch. Overrides any granted scope. |
| `POSTFACH_SEND_ALLOWLIST` | — | Comma-separated recipient patterns, e.g. `*@family.ch,boss@work.ch`. |
| `POSTFACH_MAX_RECIPIENTS` | `20` | Per message (To + Cc + Bcc). |
| `POSTFACH_MAX_SENDS_PER_DAY` | `30` | Per account, rolling 24 h. `max_sends_per_day` in the account overrides it. |
| `POSTFACH_PUBLIC_URL` | — | Public base URL; its origin is allowed by the Origin check. |
| `POSTFACH_ALLOWED_ORIGINS` | — | Extra allowed browser origins (`https://claude.ai` and `https://claude.com` are always allowed). |
| `POSTFACH_RATE_LIMIT_PER_MINUTE` | `120` | MCP requests per token (or IP) per minute. |
| `POSTFACH_DATA_DIR` | `/data` | SQLite database and default config location. |
| `POSTFACH_CONFIG` | `$POSTFACH_DATA_DIR/postfach.yaml` | Account configuration file. |
| `POSTFACH_INSECURE_TLS` | `false` | Disable IMAP/SMTP certificate validation for all accounts (homelab only; logs a warning on every connection). |

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
```

- There is no plaintext TLS mode. With `starttls`, a server that does not advertise STARTTLS is refused.
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
| `encrypt-secret` | Encrypt stdin with `POSTFACH_SECRET_KEY`. |

## Endpoints

- `POST /mcp` — Streamable HTTP MCP endpoint (stateless), bearer auth.
- `GET /healthz` — liveness.
- `GET /readyz` — database reachable and at least one account configured.
