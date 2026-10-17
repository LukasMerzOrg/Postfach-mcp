# Postfach — remote IMAP MCP server

Postfach (German for *mailbox*) is a self-hostable remote [MCP](https://modelcontextprotocol.io) server that lets
Claude search, read, triage and draft email in any IMAP mailbox, with optional sending over SMTP.

- **Generic IMAP4rev1** with capability-driven code paths (MOVE, UIDPLUS, CONDSTORE, SORT, THREAD, SPECIAL-USE,
  X-GM-EXT-1, PREVIEW) and profiles for Dovecot, Fastmail, iCloud, Infomaniak, Gmail and Microsoft 365.
- **Safe by default:** read, organize and draft are on; send and delete are off. Sending needs the server switch,
  the `mail.send` scope *and* a content-bound confirm token — there is no free-form `send_email`.
- **Token-efficient:** search returns ~60-token summary rows; bodies come as clean text on demand.
- **Prompt-injection aware:** message content is returned in `untrusted_content`, hidden HTML text is stripped and
  counted, drafts to external addresses get exfiltration warnings.
- **Works as a claude.ai custom connector:** built-in OAuth 2.1 authorization server (dynamic client registration,
  PKCE, refresh-token rotation, audience-bound tokens) with an Argon2id owner login or OIDC sign-in.
- One Docker container, SQLite volume, multiple mailboxes.

## Tools

| Tool | Purpose | Scope |
|---|---|---|
| `list_accounts` | Accounts, identities, what this connection may do | read |
| `list_folders` | Folders with special-use role and unread/total counts | read |
| `search_messages` | Structured IMAP search, newest first, cursor pagination | read |
| `get_message` | Headers, clean text body, attachments, SPF/DKIM/DMARC, phishing hint | read |
| `get_thread` | A conversation across Inbox, Sent and Archive, trimmed bodies | read |
| `get_attachment` | Text from txt/csv/ics/html/pdf; metadata only for everything else | read |
| `update_flags` | Read/unread, flag/unflag, keywords | organize |
| `move_messages` | Move or archive | organize |
| `create_draft` | New message, reply or forward into Drafts; returns preview + `confirm_token` | draft |
| `update_draft` | Replace a draft; returns a new token | draft |
| `send_draft` | Send a draft via SMTP, copy to Sent | send |
| `delete_messages` | Move to Trash (expunge only inside Trash) | delete |

Message ids are `account:folder:uidvalidity:uid`, so a reference that outlived a mailbox rebuild fails with
`stale_id` instead of touching the wrong mail.

## Quick start

```sh
IMG=ghcr.io/lukasmerzorg/postfach-mcp
docker run --rm $IMG generate-key                                    # -> POSTFACH_SECRET_KEY
printf '%s' 'a long owner password' | docker run -i --rm $IMG hash-password   # -> POSTFACH_OWNER_PASSWORD_HASH
mkdir data && cp postfach.example.yaml data/postfach.yaml            # edit accounts

docker run -d --name postfach -p 8080:8080 -v "$PWD/data:/data" \
  -e POSTFACH_PUBLIC_URL=https://postfach.example.com \
  -e POSTFACH_SECRET_KEY=... \
  -e POSTFACH_OWNER_USERNAME=lukas -e POSTFACH_OWNER_PASSWORD_HASH='$argon2id$...' \
  -e POSTFACH_PRIVATE_PASSWORD=app-password \
  $IMG
```

Put it behind TLS — [Unraid + SWAG guide](docs/deploy-unraid.md), or [`deploy/`](deploy/) for Caddy + Compose — then:

- **claude.ai / Claude apps:** Settings → Connectors → *Add custom connector* → `https://postfach.example.com/mcp`.
  Claude registers itself, you sign in with the owner login and tick the permissions to grant.
- **Claude Code:** `claude mcp add --transport http postfach https://postfach.example.com/mcp`, then `/mcp` to sign in.
- **Static token instead of OAuth** (scripts, other clients): set `POSTFACH_API_TOKEN` (from `generate-token`) and
  send `Authorization: Bearer <token>`.

Start read/draft-only. Enable sending only when you need it: `POSTFACH_ALLOW_SEND=true` **and** grant `mail.send`
on the consent page.

## Documentation

- [Deploying on Unraid behind SWAG](docs/deploy-unraid.md)
- [Configuration](docs/configuration.md) — environment variables, `postfach.yaml`, secrets, CLI
- [Providers](docs/providers.md) — Gmail, Microsoft 365, iCloud, Fastmail, Infomaniak, Dovecot
- [Security](docs/security.md) — threat model, the three send gates, prompt-injection handling

## Development

```sh
dotnet build
dotnet test tests/Postfach.Unit          # MIME corpus, sanitizer, search compiler, tokens, config
dotnet test tests/Postfach.Integration   # needs Docker: Dovecot + GreenMail via Testcontainers
```

Layout: `src/Postfach.Server` (host, MCP tools, auth, storage), `src/Postfach.Imap` (connection pool, capability
probing, search compiler, folder roles), `src/Postfach.Mime` (body extraction, HTML sanitizer, threading),
`src/Postfach.Send` (draft builder, confirm tokens, SMTP, limits), `tests/corpus` (anonymized `.eml` fixtures).

## Status

Implemented against the spec's milestones:

- **v0.1 read-only** — folders, search, message, thread, attachments; static bearer tokens; Dovecot + GreenMail CI.
- **v0.2 organize + drafts** — flags, move, archive, delete to Trash, drafts.
- **v0.3 claude.ai connector** — OAuth 2.1 authorization server built in (no shared package): RFC 8414/9728
  metadata, RFC 7591 registration with redirect allowlist, PKCE S256, consent with per-scope checkboxes,
  refresh rotation with replay detection, RFC 8707 audience binding, revocation, Argon2id or OIDC owner login.
- **v0.4 sending** — server switch + scope + confirm token, limits, allowlist, audit, injection tests.
- **v1.0 (partial)** — XOAUTH2/OAUTHBEARER mailbox auth with refresh-token rotation, provider presets,
  multi-account, GHCR image workflow.
- **Not yet:** the `/setup` page and the in-browser "Connect provider" flow for Gmail/Microsoft 365; until then
  their refresh tokens are configured in `postfach.yaml` ([docs/providers.md](docs/providers.md)).

MIT licensed.
