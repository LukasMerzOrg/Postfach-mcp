# MIME fixture corpus

Anonymized `.eml` fixtures used by the unit tests (parsing, sanitizing) and appended to the integration
servers (search, threads, attachments). Addresses use reserved `example.*` domains.

| File | Covers |
|---|---|
| `plain-utf8.eml` | 8bit UTF-8 text/plain |
| `latin1-undeclared.eml` | 8bit Latin-1 without a charset parameter |
| `encoded-headers.eml` | RFC 2047 subject/from, RFC 2231 attachment filename, CSV |
| `html-hidden.eml` | HTML only: scripts, tracking pixel, hidden text, links, blockquote |
| `nested-multipart.eml` | mixed › alternative, PDF with a text layer, zip (blocked) |
| `calendar-invite.eml` | text/calendar invite as attachment |
| `smime-signed.eml` | multipart/signed S/MIME |
| `tnef.eml` | winmail.dat (metadata only) |
| `injection.eml` | prompt-injection attempt |
| `phishing.eml` | display-name spoofing, failing and forged Authentication-Results |
| `thread-*.eml` | a three-message conversation across Inbox and Sent |
| `quoted-long.eml` | quote collapsing |
| `long-newsletter.eml` | body truncation |
