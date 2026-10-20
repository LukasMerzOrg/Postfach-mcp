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
- **Any IMAP account, set up in the browser:** the `/setup` page finds server settings from the email address
  (provider presets, Mozilla autoconfig, Thunderbird ISPDB, MX, SRV), tests the connection with plain-language
  diagnoses and stores secrets encrypted. Gmail and Microsoft accounts connect with one click via OAuth; local
  bridges (Proton Mail Bridge, DavMail) work too.
- One Docker container, SQLite volume, multiple mailboxes.

## Tools

| Tool | Purpose | Scope |
|---|---|---|
| `list_accounts` | Accounts, identities, what this connection may do | read |
| `list_folders` | Folders with special-use role and unread/total counts | read |
| `search_messages` | Structured IMAP search, newest first, cursor pagination | read |
| `get_message` | Headers, clean text body, attachments, SPF/DKIM/DMARC, phishing hint | read |
| `get_thread` | A conversation across Inbox, Sent and Archive, trimmed bodies | read |
| `get_attachment` | Text from txt/csv/ics/html/pdf, images as viewable image content; metadata only for everything else | read |
| `update_flags` | Read/unread, flag/unflag, keywords | organize |
| `move_messages` | Move or archive | organize |
| `create_draft` | New message, reply or forward into Drafts, with attachments; returns preview + `confirm_token` | draft |
| `update_draft` | Replace a draft, add or remove attachments; returns a new token | draft |
| `send_draft` | Send a draft via SMTP, copy to Sent | send |
| `delete_messages` | Move to Trash (expunge only inside Trash) | delete |

Draft attachments are either new files (`{filename, content_base64, mime_type?}`) or attachments of a message
already in the mailbox (`{message_id, part}`, copied server-side so the bytes never pass through Claude). Up to 10
per call and 20 MB per draft by default (`POSTFACH_MAX_ATTACHMENT_MB`); the preview lists each file with its size.

Message ids are `account:folder:uidvalidity:uid`, so a reference that outlived a mailbox rebuild fails with
`stale_id` instead of touching the wrong mail.

## Quick start

```sh
IMG=ghcr.io/lukasmerzorg/postfach-mcp
docker run --rm $IMG generate-key                                              # -> POSTFACH_SECRET_KEY
printf '%s' 'a long owner password' | docker run -i --rm $IMG hash-password     # -> POSTFACH_OWNER_PASSWORD_HASH

docker run -d --name postfach -p 8080:8080 -v "$PWD/data:/data" \
  -e POSTFACH_PUBLIC_URL=https://postfach.example.com \
  -e POSTFACH_SECRET_KEY=... \
  -e POSTFACH_OWNER_USERNAME=lukas -e POSTFACH_OWNER_PASSWORD_HASH='$argon2id$...' \
  $IMG
```

Run the `hash-password` pipe in a POSIX shell, such as the Unraid terminal or Git Bash. Windows PowerShell can prepend a
byte-order mark or re-encode non-ASCII characters on the way into the container, and the login then never matches.

Put it behind TLS — [Unraid + SWAG guide](docs/deploy-unraid.md) (SWAG on a custom network or Unraid's default bridge), or [`deploy/`](deploy/) for Caddy + Compose — then:

1. Open `https://postfach.example.com/setup`, sign in, enter an email address and follow the form: test, save.
   (Accounts can also be defined in `postfach.yaml`; see [configuration](docs/configuration.md).)
2. **claude.ai / Claude apps:** Settings → Connectors → *Add custom connector* → `https://postfach.example.com/mcp`.
   Claude registers itself, you sign in with the owner login and tick the permissions to grant.
3. **Claude Code:** `claude mcp add --transport http postfach https://postfach.example.com/mcp`, then `/mcp` to sign in.

A static token (`POSTFACH_API_TOKEN`, from `generate-token`) works for clients without OAuth.

Start read/draft-only. Enable sending only when you need it: `POSTFACH_ALLOW_SEND=true` **and** grant `mail.send`
on the consent page.

## Documentation

- [Deploying on Unraid behind SWAG](docs/deploy-unraid.md): template, proxy conf, bridge or custom network
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
- **v1.0** — `/setup` page with autodiscovery, connection test and in-browser "Connect" for Google, Microsoft or
  any OAuth 2 provider; XOAUTH2/OAUTHBEARER with refresh-token rotation; 16 provider presets; local bridges;
  multi-account; GHCR image. Still open: the MCP registry listing.

MIT licensed.
