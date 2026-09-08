# linkedin-connector

Node service that owns N LinkedIn sessions and speaks LinkedIn's internal
**Voyager** API on behalf of the .NET backend — the LinkedIn equivalent of
`whatsapp-connector/`. See [`../LINKEDIN_PLAN.md`](../LINKEDIN_PLAN.md) for the
full design and the ban-avoidance rules (§6).

> ⚠️ Unofficial. LinkedIn has no messaging API for personal accounts. This uses
> the logged-in user's session against undocumented endpoints. It violates the
> LinkedIn User Agreement — accounts get challenged / restricted. Run each account
> behind its own residential proxy at low volume, with explicit consent.

## What's here (Unipile-parity build)

| Module | Role |
|---|---|
| `voyager.js` | Voyager HTTP client — full cookie jar, csrf, challenge/rate-limit detection, absorbs rotated `Set-Cookie` |
| `cookies.js` | Full cookie-jar parsing — individual fields, a raw `Cookie:` header, or a browser cookie array |
| `identity.js` | Per-account fingerprint + proxy dispatcher, persisted; proxy can be rotated without losing the fingerprint |
| `proxy.js` | Managed proxy layer — `manual` / `webshare` (auto-provision a sticky residential IP per account) / `none` |
| `scheduler.js` | Per-account rolling-24h caps (send/invite/read/search), burst guards, human gap+jitter, quiet hours, 429/999 cooldown |
| `login.js` | Optional Playwright login (username/password + checkpoint) → extracts the cookie jar, then hands off to HTTP |
| `realtime.js` | Best-effort SSE stream for low-latency inbound |
| `sessionManager.js` | Ties it together, one session per account |

## Run

```bash
npm install
npm test                  # cookie / scheduler / proxy unit tests
cp .env.example .env       # set CONNECTOR_SECRET to match the backend
npm start                  # :3002

# optional — username/password login instead of cookie paste:
npm i playwright && npx playwright install chromium
```

## Connecting

`POST /accounts/:id/connect` — body is any one auth shape plus optional identity:

```jsonc
// full cookie jar (best — keeps the session alive longest)
{ "cookieHeader": "li_at=...; JSESSIONID=\"ajax:...\"; bcookie=\"...\"; ...",
  "proxyUrl": "http://user:pass@host:port" }

// individual cookie fields
{ "li_at": "...", "jsessionid": "ajax:...", "proxyUrl": "..." }

// username / password (needs Playwright) — may return { status: "waiting_for_credentials", checkpoint: true }
{ "username": "me@example.com", "password": "...", "proxyUrl": "..." }
```

Optional on any of them: `userAgent`, `acceptLang`, `timezone` (IANA, drives quiet
hours), `country` (for `PROXY_PROVIDER=webshare`).

With `PROXY_PROVIDER=webshare` the proxy is auto-assigned — omit `proxyUrl`.

**Checkpoint:** if connect returns `checkpoint: true`, submit the emailed / SMS /
authenticator code:

```
POST /accounts/:id/challenge   { "code": "123456" }
```

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/accounts/:id/connect` | connect / resume (body above) |
| `POST` | `/accounts/:id/challenge` | submit a login checkpoint code |
| `GET`  | `/accounts/:id/qr` | status poll (`qr` always `null`) |
| `POST` | `/accounts/:id/send` | `{ to, text }` — `to` = thread id `2-…==` or member URN/id |
| `POST` | `/accounts/:id/logout` | end session, wipe `sessions/<id>/` |
| `GET`  | `/health` `/sessions` `/debug/events` | diagnostics (`/sessions` shows scheduler headroom) |

All non-`/health` routes require `X-Connector-Secret`.

## Session files (`sessions/<id>/`)

`cookie.json` (full jar, refreshed as LinkedIn rotates cookies), `identity.json`
(proxy + fingerprint + timezone), `usage.json` (rolling action counts — survives
restarts so caps aren't reset).

## Not implemented (phase 2+)

Media send/receive hosting, reactions, group-thread metadata, fleet
orchestration across multiple connector processes, per-account proxy managed via
the unified API.
