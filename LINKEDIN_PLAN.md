# Plan: Add LinkedIn as a channel (the "WhatsApp way")

Goal: connect a **personal LinkedIn account** and expose its messaging through the
same unified REST API + signed webhook as WhatsApp — no official LinkedIn API,
just the logged-in-user session, a dedicated Node connector, and a `.NET` adapter.

> ⚠️ Same caveat as WhatsApp, worse. LinkedIn has **no messaging API for personal
> accounts**. This uses LinkedIn's internal *Voyager* endpoints with the user's
> session cookie. It violates the LinkedIn User Agreement; accounts get
> challenged, rate-limited, or restricted. Don't use a profile you care about.
>
> The only way to *eliminate* ban risk is the official **LinkedIn Messaging API**
> (Partner Program — approval-gated, usually rejected). This plan takes the
> **unofficial, risk-reduced path** ("Option B"): per-account residential proxy,
> stable per-account browser fingerprint, strict rate limits, explicit user
> consent. That makes a ban *uncommon*, never *impossible* — see §6.

---

## 1. How the WhatsApp pattern maps onto LinkedIn

| WhatsApp | LinkedIn equivalent | Notes |
|---|---|---|
| QR scan (WhatsApp Web protocol) | Paste `li_at` (+ `JSESSIONID`) session cookie | **No QR.** The "connect" step submits credentials instead of rendering an image. Optional: username/password login with challenge handling (fragile). |
| Baileys WebSocket | Voyager REST + realtime SSE stream (`/realtime/connect`) | Long-lived `EventSource` per account pushes new-message events. |
| `sessions/<id>/` multi-file auth state | `sessions/<id>/cookies.json` (encrypted at rest) | Persist cookie + derived CSRF token so restarts don't need a re-paste. |
| `loggedOut` disconnect reason | Voyager returns `401` / `999` / CSRF failure | Connector reports `logged_out` → account `needs_reauth` → webhook. |
| remote JID `…@s.whatsapp.net` / `…@g.us` | conversation URN `urn:li:msg_conversation:(urn:li:fsd_profile:…,2-…==)` (thread id `2-…==`) | `ChatId` = thread id (stable, URL-safe). |
| participant JID | member URN `urn:li:fsd_profile:<id>` | `SenderId`. |
| polls | *not supported* | `SendPollAsync` throws `NotSupportedException`. |
| reactions (emoji) | message reactions exist in Voyager | Phase 2. |
| group metadata / admin events | group threads exist, no admin model | Minimal `group.*` support; phase 2. |

Everything downstream of `IMessagingProviderAdapter` (unified model, dispatcher,
retry, HMAC signing, conversations inbox, SignalR) is already provider-agnostic
and needs **no changes**.

---

## 2. New component: `linkedin-connector/` (Node)

Mirror `whatsapp-connector/` structure. One process, N LinkedIn sessions.

```
linkedin-connector/            # IMPLEMENTED — express + pino + undici only
  package.json  .env.example
  src/
    index.js            # express app + shared-secret gate; connect / challenge / send / logout
    config.js           # all knobs (proxy provider, daily caps, quiet hours, login toggles)
    backend.js          # reportStatus -> /api/internal/linkedin/status ; forwardMessage -> /api/webhooks/linkedin
    voyager.js          # Voyager client: me, conversations, events, send; absorbs rotated Set-Cookie
    cookies.js          # full cookie-jar parsing (fields | Cookie: header | browser array)
    identity.js         # per-account fingerprint + proxy dispatcher, persisted; proxy hot-swap
    proxy.js            # managed proxy layer: manual | webshare (auto-provision) | none
    scheduler.js        # rolling-24h caps (send/invite/read/search) + burst guards + gap/jitter + quiet hours + 429 cooldown
    login.js            # OPTIONAL Playwright login (user/pass + checkpoint) -> extracts the jar
    realtime.js         # best-effort SSE stream
    sessionManager.js   # one session per account; ties it all together
  test/                 # node --test — cookies / scheduler / proxy (18 tests)
```

Per-account **identity** (proxy + fingerprint) is the core ban-avoidance
mechanism — see §6. Set at connect time, persisted in `sessions/<id>/identity.json`,
so every request and the SSE stream for that account always egress from the same
IP with the same fingerprint. The **full cookie jar** (not just `li_at`) lives in
`sessions/<id>/cookie.json` and is rewritten whenever LinkedIn rotates a cookie.
`sessions/<id>/usage.json` holds the rolling action counts so a restart doesn't
reset the daily caps.

### Voyager surface used (all under `https://www.linkedin.com/voyager/api/`)

| Purpose | Endpoint |
|---|---|
| Validate session, get own member URN | `GET /me` |
| List conversations | `GET /messaging/conversations` (paged by `createdBefore`) |
| Fetch a conversation's events | `GET /messaging/conversations/<threadId>/events` |
| Send a message | `POST /messaging/conversations/<threadId>/events?action=create` with `{ eventCreate: { value: { "com.linkedin.voyager.messaging.create.MessageCreate": { body, attachments: [] } } } }` |
| Create a new 1:1 thread | `POST /messaging/conversations?action=create` with recipient member URN |
| Mark read | `POST /messaging/conversations/<threadId>?action=markRead` |
| Realtime stream | `GET https://www.linkedin.com/realtime/connect?rc=1` header `accept: text/event-stream` |

Required headers on every call: `cookie: li_at=…; JSESSIONID="ajax:…"`,
`csrf-token: ajax:…` (value from `JSESSIONID`), `x-restli-protocol-version: 2.0.0`,
`x-li-lang`, plus this account's **persisted** `user-agent` and `x-li-track`
(device blob) from `identity.js` — never regenerated per boot. All traffic
(Voyager + SSE) goes through this account's `undici` `ProxyAgent`.

### Realtime → message normalization

The SSE stream emits `com.linkedin.realtimefrontend.DecoratedEvent`. For
`topic` containing `messagingConversations`/`messages`, pull
`payload.data["doc"]` / `eventContent` → shape a raw event and
`forwardMessage(accountId, raw)`. `raw` should carry enough for the adapter:

```jsonc
{
  "threadId": "2-abc==",
  "eventUrn": "urn:li:msg_event:(urn:li:msg_conversation:(…),<id>)",
  "from": "urn:li:fsd_profile:XXXX",
  "fromMe": false,
  "subject": null,
  "body": { "text": "hey there" },
  "attachments": [],
  "createdAt": 1757000000000
}
```

Also poll `GET /messaging/conversations` every ~90s (`POLL_INTERVAL_MS`, with
jitter) as a backstop for missed SSE events — dedupe by `eventUrn`, and the
backend already dedupes on `(AccountId, ProviderMessageId)`.

### Connector HTTP endpoints (same shape as WhatsApp connector)

| Method | Path | Body | Purpose |
|---|---|---|---|
| `POST` | `/accounts/:id/connect` | `{ "li_at": "…", "jsessionid": "…", "proxyUrl"?, "userAgent"? }` *(or `{ "username", "password" }`)* | Store cookie + build/persist identity, validate via `/me` **through the proxy**, start realtime stream. |
| `GET` | `/accounts/:id/qr` | — | Status poll. Returns `{ status, qr: null }` (keeps the adapter contract; `qr` is always null). |
| `POST` | `/accounts/:id/send` | `{ to, text }` (`to` = threadId **or** member URN) | Send text; phase 2 adds `media`. |
| `POST` | `/accounts/:id/logout` | — | Drop stream, wipe `sessions/<id>/`. |
| `GET` | `/accounts/:id/conversations` | — | Optional passthrough for the console. |

Auto-reconnect: on SSE drop, exponential backoff (reuse the WhatsApp connector's
`reconnectBaseDelay`/`reconnectMaxDelay` logic). On `401`/CSRF failure → stop,
`reportStatus(accountId, "logged_out")`. On `429` or LinkedIn `999` → pause that
account's `ratelimit` bucket for a cooldown, do **not** retry-hammer. On a
challenge/checkpoint response body → `reportStatus(accountId, "logged_out")`
immediately and halt all requests for that account.

---

## 3. Backend changes (`backend/UnifiedMessaging.Api`)

### 3.1 New adapter — `Adapters/LinkedIn/`

**`LinkedInConnectorClient.cs`** — typed `HttpClient` over the connector, same as
`WhatsAppConnectorClient` (`ConnectAsync`, `GetStatusAsync`, `SendTextAsync`,
`LogoutAsync`). `ConnectAsync` takes the credential blob.

**`LinkedInAdapter.cs`** — `IMessagingProviderAdapter`, `ProviderName => "linkedin"`.

- `ConnectAccountAsync` — POST credentials to connector, return
  `ConnectResult(accountId, status)` (no QR, no redirect). If no credentials were
  supplied yet, return `AccountStatus.WaitingForCredentials` (new constant).
- `GetConnectStatusAsync` — map connector status → `AccountStatus`
  (`connected` / `waiting_for_credentials` / `disconnected` / `logged_out`→`NeedsReauth`).
- `SendMessageAsync` — normalize `chatId`, call connector `/send`, return an
  outbound `UnifiedMessage` stub with `ProviderMessageId` = returned event id
  (so the realtime echo dedupes).
- `SendMediaAsync` — Phase 2; throw `NotSupportedException` for now.
- `SendPollAsync` — `throw new NotSupportedException("LinkedIn has no polls")`.
- `ParseIncoming(JsonElement payload)` — read `{ accountId, raw }`, map:
  - `ChatId` = `raw.threadId`
  - `ProviderMessageId` = `raw.eventUrn`
  - `SenderId` = `raw.fromMe ? "me" : raw.from`
  - `Text` = `raw.body.text`
  - `Direction` = `raw.fromMe ? Outbound : Inbound`
  - `Timestamp` = epoch-ms `raw.createdAt`
  - `Attachments` = `[]` for phase 1
  - Return `null` when there's no text and no attachment (same guard as WhatsApp).

Unit-test it with `LinkedInAdapterTests` (copy `WhatsAppAdapterTests.cs`), feeding
captured realtime payloads.

### 3.2 `Program.cs`

```csharp
var linkedInConnectorBaseUrl =
    builder.Configuration["LinkedInConnector:BaseUrl"] ?? "http://localhost:3002";

builder.Services.AddHttpClient<LinkedInConnectorClient>(c =>
{
    c.BaseAddress = new Uri(linkedInConnectorBaseUrl);
    c.DefaultRequestHeaders.Add("X-Connector-Secret", connectorSecret);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<IMessagingProviderAdapter, LinkedInAdapter>();
```

`ProviderResolver` picks it up automatically.

### 3.3 Generalize the internal status route

Today: `group.MapPost("/internal/whatsapp/status", …)`.

Change to `group.MapPost("/internal/{provider}/status", …)` (same handler — it's
already provider-neutral; it just reads `ConnectorStatusUpdate`). Keeps the
WhatsApp connector working and lets the LinkedIn connector POST to
`/api/internal/linkedin/status`. The shared-secret gate in `Program.cs` already
matches on `/api/internal` prefix, so no auth change.

### 3.4 `AccountStatus` — add a constant

```csharp
public const string WaitingForCredentials = "waiting_for_credentials";
```

Map it in `WebhookIngestEndpoints` status handler and `AccountEndpoints`
`GetConnectStatusAsync` switch. `message`-send guard already blocks anything not
`connected`, so nothing else needs touching.

### 3.5 Connect flow — accept credentials

`ConnectRequest` gains an optional bag:

```csharp
public record ConnectRequest(
    Guid AccountId, string Provider, string? DisplayName = null,
    Dictionary<string, string>? Credentials = null);
```

`AccountEndpoints` `POST /{id}/connect` reads an optional JSON body and passes
`Credentials` through. For WhatsApp the body stays empty — behaviour unchanged.
For LinkedIn the bag carries the cookie **and the ban-avoidance identity**:
`POST /api/accounts/{id}/connect { "li_at": "…", "jsessionid": "…", "proxyUrl": "http://user:pass@host:port", "userAgent": "…" }`.

Credentials + identity are **not persisted by the backend** — they go straight to
the connector, which owns the session store (exactly like WhatsApp creds). No DB
migration required. (A per-account proxy column on `UnifiedAccount` is only worth
adding in Phase 3 if operators want to manage proxies from the API rather than
re-supply on connect.)

### 3.6 Things that stay WhatsApp-only (leave gated)

- `/groups`, `/groups/{jid}` endpoints — already `400` for non-WhatsApp.
- `/reactions` endpoint — uses `WhatsAppConnectorClient` directly. Phase 2:
  branch on provider or add `LinkedInConnectorClient.SendReactionAsync`.
- `ConversationService.ApplyMessageAsync` infers `IsGroup` from `@g.us`. For
  LinkedIn a group thread has multiple `participants`; pass an explicit
  `isGroup` hint later. Harmless for phase 1 (everything shows as 1:1).

### 3.7 `wwwroot/index.html` console

When `account.provider === "linkedin"` and status is `waiting_for_credentials`,
show a **connect form** instead of the QR image: `li_at`, `JSESSIONID`,
`proxyUrl` (required), `userAgent` (optional, defaults to a current Chrome UA),
plus a short "how to copy your cookie" note and a **"LinkedIn may restrict this
account" consent checkbox**. POST to `/connect`, then resume the existing
status-polling / SignalR flow. Everything else in the console already works off
the unified endpoints.

---

## 4. Config & tooling

**Backend** (`appsettings.json`):

| Key | Default |
|---|---|
| `LinkedInConnector__BaseUrl` | `http://localhost:3002` |
| `Connector__SharedSecret` | *(shared with WhatsApp)* |

**Connector** (`linkedin-connector/.env.example`): `PORT=3002`, `BACKEND_URL`,
`CONNECTOR_SECRET`, `SESSIONS_DIR`, `DEFAULT_USER_AGENT`, `RL_SENDS_PER_HOUR`
(`20`), `RL_READS_PER_MIN` (`10`), `CAP_SEND_PER_DAY` (`100`), `CAP_INVITE_PER_DAY`
(`25`), `QUIET_HOURS` (e.g. `22-7`), `POLL_INTERVAL_MS` (`90000`), `PROXY_PROVIDER`
(`manual` | `webshare` | `none`), `LOGIN_ENABLED`. With `manual`, the proxy is
**per-account** (supplied on connect / stored in `identity.json`); with `webshare`
it's auto-provisioned from the provider pool. Never let two accounts share an IP.

**`Makefile`**:

```make
install:
	cd whatsapp-connector && npm install
	cd linkedin-connector && npm install

linkedin-connector:  ## Run the LinkedIn connector on :3002
	cd linkedin-connector && npm start
```

**`README.md`** — add LinkedIn to the "Adding the next provider" table (change
Difficulty note), document the cookie-connect flow, add a `requests.http` block.

---

## 5. Phasing

**Phase 0 — Spike (still needed).** Paste a real cookie **behind a residential
proxy**, hit `/voyager/api/me` and `/messaging/conversations`, send yourself a DM,
open `/realtime/connect`. Capture 3–4 real payloads and **check the field paths
in `voyager.js` / `sessionManager.js`'s `extract*` helpers against them** — those
normalizers are written defensively but unverified against live LinkedIn. Confirm
a datacenter IP gets challenged and the proxy doesn't.

**Phase 1 — DONE.** Receive + send text, with ban-avoidance built in:
- `linkedin-connector/` service — connect (cookie / full jar / username+password),
  `/challenge`, send, logout, auto-reconnect, `logged_out` → reauth.
- Per-account **proxy** (`proxy.js`: manual / webshare / none) + persisted
  **fingerprint** (`identity.js`) + **full cookie jar** (`cookies.js`, absorbs
  rotated `Set-Cookie`).
- **Rate-limit scheduler** (`scheduler.js`) — rolling-24h caps, burst guards,
  gap+jitter, quiet hours, `429`/`999` cooldown; `usage.json` survives restarts.
- **Browser-assisted login** (`login.js`) — optional Playwright, checkpoint relay.
- Backend — `LinkedInAdapter` + `LinkedInConnectorClient`, `Program.cs` wiring,
  `/internal/{provider}/status`, `WaitingForCredentials`, `ConnectRequest.Credentials`,
  challenge routing (re-`POST /connect` with `{ challengeCode }`).
- Console — provider selector + connect form (cookie + proxy + consent).
- Tests — `LinkedInAdapterTests` (7, C#) + connector `test/` (18, node:test).

**Phase 2 — Parity features (not started).**
- Inbound + outbound attachments (Voyager media upload → host like the WhatsApp connector).
- Message reactions (`message.reaction_added` / `_removed`).
- Read receipts / mark-read wiring for the conversations inbox.
- Group threads: participant list, `group.*` events where they exist.
- InMail vs. regular message distinction (flag on the payload).
- Webshare driver: verify against a live account; add more provider drivers.

**Phase 3 — Hardening (not started).**
- Move proxy config onto `UnifiedAccount` (migration) so operators manage/rotate
  proxies via the API without a re-connect.
- Fleet orchestration: assign accounts to N connector processes, rebalance,
  re-hydrate sessions on restart, shared session store (Redis/DB) instead of files.
- `LOGIN_HEADLESS=false` + a persistent browser-context mode as a fallback when
  raw Voyager starts getting challenged for an account.
- `AccountHealthWorker` already escalates stale `disconnected` accounts — verify
  the LinkedIn `logged_out` path reaches `needs_reauth` cleanly.

---

## 6. Staying unbanned (Option B — the unofficial, risk-reduced path)

Bans can't be eliminated without the official Partner API. The goal here is to
make a restriction *uncommon* and *contained to one account*. Five levers, in
priority order:

### 6.1 Network identity — one residential/mobile proxy per account
A datacenter/server IP is an instant tell (and in Phase 0 you'll confirm it gets
challenged). Each account gets **its own sticky residential or mobile proxy**,
geo-matched to where that user normally logs in. Enforced by `identity.js` +
`undici` `ProxyAgent` on every Voyager call **and** the SSE stream. Hard rule:
**two accounts never share an egress IP.** `proxy.js` resolves it: `manual`
refuses `connect` without a `proxyUrl`; `webshare` auto-assigns a sticky endpoint
per account (deterministic hash → one IP from the pool) so the operator never
pastes a proxy string — the Unipile model.

### 6.2 Browser fingerprint — stable, realistic, per account
Persist `user-agent`, `x-li-track` device blob, `accept-language`,
`x-li-page-instance` in `sessions/<id>/identity.json` at connect time and reuse
them forever. Never regenerate on restart. Match the header set and query params
the real LinkedIn web app sends (`x-restli-protocol-version: 2.0.0`, decoration
IDs, `x-restli-method`).

### 6.3 Volume & rhythm — `ratelimit.js`
- Per-account token bucket: default ≤20 sends/hour, ≤10 reads/min (`RL_*` env).
- Jitter every call; never a tight loop. Conversation-poll backstop at ~90s, not
  seconds.
- **No cold outreach, no bulk / templated blasts** — the single fastest way to a
  restriction. The unified `POST /messages` is for replies and 1:1 conversation,
  not campaigns. Consider rejecting sends to members the account has no existing
  thread with.
- A session that *only* ever makes API calls is abnormal — expect the user to
  keep using LinkedIn normally in a browser too.

### 6.4 React to early warnings, don't push through them
| Signal | Connector action |
|---|---|
| HTTP `429` / LinkedIn `999` | Pause the account's bucket for a cooldown (minutes → escalating). No retry storm. |
| Challenge / checkpoint HTML in a response | `reportStatus(accountId, "logged_out")` at once, halt all requests, fire `account.needs_reauth`. |
| SSE `401` / CSRF failure | Same — stop, reauth. |
| Repeated soft-blocks in a window | Flip the account to `needs_reauth` and let a human decide. |

### 6.5 Contain the blast radius
- Every customer connects **their own** LinkedIn account — you never run a pool
  of your own profiles that can all fall together.
- Explicit consent: the connect form carries a "LinkedIn may restrict this
  account" checkbox; the README says it plainly.
- If raw Voyager starts drawing challenges for an account, switch just that
  account to `SESSION_MODE=playwright` (Phase 3) — a real headless browser is far
  harder to fingerprint.

### 6.6 Recovery when an account *is* restricted
Nothing server-side fixes it — it's the end user's job, in a browser:
1. Log into LinkedIn normally, clear the checkpoint (email PIN / phone / ID).
2. Temporary restriction → wait it out (24h–days).
3. Permanent → LinkedIn's appeal flow (slow, often denied).
4. Back in → generate a fresh `li_at`, re-submit on the console → connector
   resumes on the same proxy/identity.

The `needs_reauth` → re-connect path (already mirrored from WhatsApp's
`loggedOut`) is what makes this survivable operationally.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| Account restriction / ban | The whole of §6 — per-account residential proxy, stable fingerprint, strict rate limits, no bulk send, early-warning backoff, explicit consent. Uncommon, never zero. |
| Cookie expiry (weeks, or instant on IP change) | Sticky proxy keeps the IP stable; on expiry `logged_out` → `account.needs_reauth` webhook → console prompts re-paste. |
| Voyager schema drift | Keep `RawJson`; normalize defensively (same null-tolerant style as `WhatsAppAdapter`); fixture-based tests. |
| Realtime stream silently dies | ~90s conversation-poll backstop + dedupe on `eventUrn`. |
| CAPTCHA / challenge on login | Cookie auth is the primary path; password login is best-effort; challenge response → immediate `needs_reauth`. |
| Proxy provider quality (blocklisted resi IPs) | Use a reputable residential/mobile provider; monitor per-account challenge rate; rotate the proxy (not the identity) if one goes bad. |
| No official support, ToS violation | Product/legal decision — "hard, fragile" channel by design, like WhatsApp. Official Messaging API remains the only zero-risk route. |

---

## 8. File checklist

**New — all created**
- `linkedin-connector/` — package.json, .env.example, .gitignore, README.md,
  src/{index,config,backend,voyager,cookies,identity,proxy,scheduler,login,realtime,sessionManager}.js,
  test/{cookies,scheduler,proxy}.test.js
- `backend/UnifiedMessaging.Api/Adapters/LinkedIn/LinkedInAdapter.cs`
- `backend/UnifiedMessaging.Api/Adapters/LinkedIn/LinkedInConnectorClient.cs`
- `backend/UnifiedMessaging.Api.Tests/LinkedInAdapterTests.cs`

**Edited**
- `Program.cs` (HttpClient + adapter registration, `LinkedInConnector:BaseUrl`)
- `Adapters/IMessagingProviderAdapter.cs` + `WhatsAppAdapter.cs` (`LogoutAsync` on the interface)
- `Endpoints/WebhookIngestEndpoints.cs` (`/internal/{provider}/status`, `waiting_for_credentials` map)
- `Endpoints/AccountEndpoints.cs` (connect body → `Credentials`; logout via adapter)
- `Contracts/Contracts.cs` (`ConnectRequest.Credentials`), `Models/UnifiedModels.cs` (`WaitingForCredentials`)
- `appsettings.json`, `wwwroot/index.html` (provider selector + connect form)
- `Makefile`, `README.md`, `requests.http`

**No migration** in Phases 1–2. Phase 3 optionally adds a proxy column to
`UnifiedAccount` (migration) so proxies are managed via the API instead of
re-supplied on connect.
