# Deploying on Unraid behind SWAG

The result: `https://postfach.example.com/mcp` added as a custom connector in claude.ai, served by SWAG, with
Postfach running as an Unraid container on the same Docker network. Files used:

- [`deploy/unraid/postfach.xml`](../deploy/unraid/postfach.xml) — Unraid Docker template
- [`deploy/swag/postfach.subdomain.conf`](../deploy/swag/postfach.subdomain.conf) — SWAG proxy config
- [`deploy/unraid/postfach.example.yaml`](../deploy/unraid/postfach.example.yaml) — minimal account config

## 0. Prerequisites

- SWAG is running and working for another subdomain, attached to a **custom Docker network** (the linuxserver
  guides call it `proxynet`; check yours under *Docker → swag → Network Type*).
- DNS: `postfach.example.com` points at your public IP (or a CNAME to your DDNS name), and SWAG's certificate
  covers it — add `postfach` to SWAG's `SUBDOMAINS` variable unless you use a wildcard certificate, then restart
  SWAG and check its log for a successful renewal.
- The image `ghcr.io/lukasmerzorg/postfach-mcp` is pullable. GHCR packages of an organization start out
  **private**: either make it public (GitHub → organization → Packages → postfach-mcp → Package settings →
  Change visibility) or log in once on Unraid with a read-only token:
  `docker login ghcr.io -u <github-user>` (password: a classic PAT with `read:packages`).

## 1. Generate the secrets (Unraid terminal)

```sh
IMG=ghcr.io/lukasmerzorg/postfach-mcp:latest
docker run --rm $IMG generate-key
# -> e.g. VQ6op/Q9...=   (POSTFACH_SECRET_KEY: store a copy in your password manager)

printf '%s' 'a long owner password' | docker run -i --rm $IMG hash-password
# -> $argon2id$v=19$m=19456,t=2,p=1$...   (POSTFACH_OWNER_PASSWORD_HASH)
```

Use a password manager entry for the owner password; it is what you type on the consent page when connecting
Claude.

## 2. Create appdata and the account config

```sh
mkdir -p /mnt/user/appdata/postfach
nano /mnt/user/appdata/postfach/postfach.yaml     # start from deploy/unraid/postfach.example.yaml
chown -R 99:100 /mnt/user/appdata/postfach
chmod 600 /mnt/user/appdata/postfach/postfach.yaml
```

Mailbox passwords: either reference a container variable (`password_env: POSTFACH_PRIVATE_PASSWORD`, filled in
the template) or encrypt them so they never sit in plain text on the flash drive:

```sh
printf '%s' 'mailbox-app-password' | docker run -i --rm -e POSTFACH_SECRET_KEY='<key from step 1>' $IMG encrypt-secret
# -> enc:v1:...   then in postfach.yaml:  password_enc: enc:v1:...
```

See [providers.md](providers.md) for Gmail / Microsoft 365 (OAuth) and iCloud (app password).

## 3. Add the container

```sh
wget -O /boot/config/plugins/dockerMan/templates-user/my-postfach.xml \
  https://raw.githubusercontent.com/LukasMerzOrg/Postfach-mcp/main/deploy/unraid/postfach.xml
```

(If the repository is private, copy the file over SMB instead.) Then *Docker → Add Container → Template:
postfach* and fill in:

| Field | Value |
|---|---|
| Network Type | the same custom network as SWAG (template default: `proxynet`) |
| Public URL | `https://postfach.example.com` (no trailing slash) |
| Secret key | from step 1 |
| Owner username / password hash | from step 1 — paste the hash as is; the Unraid form does not need `$` escaping |
| Allow sending | `false` for now |
| Mailbox password 1 | only if `postfach.yaml` uses `password_env: POSTFACH_PRIVATE_PASSWORD` |

Keep the container name `postfach` — SWAG resolves it by that name. The template runs the container as
`99:100` (nobody:users) and does not publish a port; SWAG talks to it over the Docker network on port 8080.

After *Apply*, the container log should show `Postfach starting with 1 account(s)` and
`OAuth authorization server enabled at https://postfach.example.com`.

## 4. Add the SWAG proxy config

```sh
wget -O /mnt/user/appdata/swag/nginx/proxy-confs/postfach.subdomain.conf \
  https://raw.githubusercontent.com/LukasMerzOrg/Postfach-mcp/main/deploy/swag/postfach.subdomain.conf
docker restart swag
```

The config disables buffering for `/mcp` (MCP responses can be server-sent events) and keeps `/healthz` and
`/readyz` LAN-only. **Do not add Authelia/Authentik** to it: Postfach is its own OAuth server, and claude.ai must
reach `/mcp`, `/oauth/*` and `/.well-known/*` without a login wall in front. SWAG's `proxy.conf` sends
`X-Forwarded-For`; Postfach trusts it from Docker/private networks (`POSTFACH_TRUSTED_PROXIES`), so rate limits and
the audit log see real client IPs.

## 5. Check from outside

```sh
curl -s https://postfach.example.com/.well-known/oauth-protected-resource
# {"resource":"https://postfach.example.com/mcp","authorization_servers":["https://postfach.example.com"],...}
curl -si -X POST https://postfach.example.com/mcp | grep -i www-authenticate
# WWW-Authenticate: Bearer realm="postfach", resource_metadata="https://postfach.example.com/.well-known/..."
```

## 6. Connect Claude

- **claude.ai** (web, then also mobile/Desktop): Settings → Connectors → *Add custom connector* → name
  `Postfach`, URL `https://postfach.example.com/mcp` → *Connect*. Sign in with the owner login and tick the
  permissions. Leave *send* and *delete* unticked to start.
- **Claude Code:** `claude mcp add --transport http postfach https://postfach.example.com/mcp`, then run `/mcp`
  and choose *Authenticate*.

To change permissions later, disconnect and reconnect the connector. `docker exec postfach dotnet
Postfach.Server.dll revoke-oauth` signs out every client.

## Enabling sending later

Set *Allow sending* to `true` in the template (optionally *Send allowlist*), apply, then reconnect the connector
and tick `mail.send`. Every send still needs a draft preview that you approve in the chat.

## Updates and backups

- Update: *Docker → postfach → Force update* (or the Unraid update notice). Tokens, clients and keys survive in
  appdata.
- Back up `/mnt/user/appdata/postfach` (Appdata Backup plugin is fine) **and** the secret key separately. Without the
  key, `password_enc` values and stored refresh tokens cannot be decrypted; OAuth clients just reconnect.

## Troubleshooting

| Symptom | Cause |
|---|---|
| SWAG returns 502 | Container not named `postfach`, or not on SWAG's network. `docker exec swag ping -c1 postfach` |
| Certificate error for postfach.* | `postfach` missing from SWAG `SUBDOMAINS` / DNS not pointing at you |
| claude.ai: "redirect URI not allowed" | Custom `POSTFACH_OAUTH_REDIRECT_ALLOWLIST` without the claude.ai callback |
| Consent page says "form expired" | Opened over plain http or a different host than `Public URL`; cookies are `Secure` |
| Login always fails | Hash copied incompletely (must start with `$argon2id$` and contain five `$` sections) |
| `readyz` reports `accounts: 0` | `postfach.yaml` missing, not readable by 99:100, or a YAML error (see container log) |
| `backend_unavailable` from tools | Mailbox host/port/password; check the container log for the IMAP error |
