# Security

Anyone on the internet can put text into your inbox, so every message body is an attacker-controlled prompt.
Postfach assumes Claude will sometimes be fooled and limits what a fooled Claude can do.

## Sending: three independent gates

1. **Server switch** — `POSTFACH_ALLOW_SEND=false` by default; it overrides any scope.
2. **Scope** — the token must carry `mail.send` (not in the default grant).
3. **Content-bound confirm token** — `create_draft` / `update_draft` return an HMAC over
   *draft id + content hash + expiry* (15 min). `send_draft` re-fetches the draft from IMAP, recomputes the hash
   over From/To/Cc/Bcc, subject, bodies and attachments and rejects the token if anything changed — including an
   edit made in another mail client. Tokens are single-use.

There is no free-form send: it is always draft → review → send, and the draft is visible in the user's own mail
client. Additional limits: max 20 recipients per message, 30 sends per day per account, optional recipient
allowlist, the From address must be a configured identity. Every send is recorded in the audit table.

## Prompt injection

- Bodies and attachment text are returned only inside `untrusted_content`; tool descriptions and the server
  instructions tell the model to treat it (and subjects, snippets, names) as data.
- HTML is never returned raw. Scripts, styles, images and tracking pixels are dropped; hidden text
  (`display:none`, `visibility:hidden`, zero size or opacity, white-on-white) is removed and counted in
  `hidden_text_removed`.
- No tool fetches URLs, follows links or loads remote images.
- `create_draft` adds `warnings[]` when a draft to an external address contains long encoded strings or long URL
  query strings, quotes other messages in a new mail, or forwards a message to someone who was not on it.
- `get_message` reports the receiving server's SPF/DKIM/DMARC verdict (topmost `Authentication-Results` only) and
  `sender_mismatch` when the display name claims a different domain than the address.

## Reading never changes state

Read tools open folders with `EXAMINE` and fetch with `BODY.PEEK`, so reading never sets `\Seen` or clears
`\Recent`. All commands are UID-based.

## Deleting

`delete_messages` moves to Trash. Only messages already in Trash are expunged, and only by UID
(`UID EXPUNGE`, UIDPLUS). Without MOVE and UIDPLUS, moves fail rather than issuing a plain `EXPUNGE` that could
remove other messages marked `\Deleted`.

## Secrets

- Mailbox passwords and refresh tokens are read from env vars or `enc:v1:` values (AES-256-GCM, key derived with
  HKDF from `POSTFACH_SECRET_KEY`). They never appear in tool I/O or logs.
- API tokens are stored as SHA-256 hashes and compared in constant time.
- Credentials are only sent over TLS; STARTTLS downgrade is refused; certificate validation is always on unless
  `insecure_tls` / `POSTFACH_INSECURE_TLS` is set, which logs a warning on every connection.

## Platform

TLS terminates at the reverse proxy. The MCP endpoint checks `Origin` (DNS-rebinding/CSRF), rate-limits per token,
returns scrubbed errors, and logs JSON without message content. The audit table (`/data/postfach.db`) keeps tool
writes and sends for 90 days.

## Recommended rollout

1. Read-only token (`mail.read`) for a week.
2. Add `mail.organize` and `mail.draft`; review drafts in your mail client and send them yourself.
3. Only then consider `POSTFACH_ALLOW_SEND=true` with a `mail.send` token, ideally with `POSTFACH_SEND_ALLOWLIST`.
