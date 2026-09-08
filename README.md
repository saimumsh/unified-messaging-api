# Unified Messaging API

A Unipile-style unified messaging API: normalize many messaging channels into **one
REST API** and **one signed webhook format** for your customers.

**First channel: WhatsApp**, via the QR-code / personal login method (WhatsApp Web
protocol) — not the official Meta Cloud API.

> ⚠️ The QR method connects through WhatsApp's consumer protocol. It violates
> WhatsApp's Terms of Service and numbers can be banned or rate-limited without
> warning. Don't test with a primary/personal number.

---

## Architecture

```
                    REST  /api/accounts, /api/webhooks/{provider}, ...
customer  ───────────────────────────────────────────────►  ┌───────────────────────┐
                                                            │  .NET backend         │
                                                            │  (backend/)           │
signed webhook  ◄───────────────────────────────────────────│                       │
  X-Unified-Signature: sha256=...                            │  • UnifiedAccount /    │
                                                            │    UnifiedMessage     │
                        internal REST + shared secret        │  • provider adapters  │
                    ┌──────────────────────────────────────► │  • webhook dispatcher │
                    │   POST /accounts/:id/connect|send       │    (retry + HMAC +    │
                    │   ◄── QR image, "sent"                  │     dead-letter)      │
       ┌────────────┴─────────┐                               │  • PostgreSQL (EF)    │
       │ Node + Baileys        │  ── POST /api/webhooks/whatsapp ──►                   │
       │ connector             │  ── POST /api/internal/whatsapp/status ──►            │
       │ (whatsapp-connector/) │                               └───────────────────────┘
       │ one process, N        │
       │ WhatsApp sessions     │ ◄──WebSocket──►  WhatsApp
       └───────────────────────┘
```

Node owns the raw WhatsApp socket per account (no mature .NET library speaks the
multi-device protocol). .NET is the brain: unified model, adapters, database,
customer webhook fan-out.

---

## Layout

| Path | What |
|------|------|
| `backend/UnifiedMessaging.Api` | ASP.NET Core minimal API, EF Core + PostgreSQL, SignalR |
| `backend/UnifiedMessaging.Api.Tests` | xUnit — Baileys payload normalization, webhook signing |
| `whatsapp-connector/` | Node + Baileys connector service |
| `linkedin-connector/` | Node + Voyager connector service (LinkedIn, unofficial — see `LINKEDIN_PLAN.md`) |
| `docker-compose.yml` | PostgreSQL 17 |
| `requests.http` | End-to-end request walkthrough |

---

## Run it locally

Prereqs: .NET 10 SDK, Node 20+, Docker.

```bash
# 1. Database
make db                 # or: docker compose up -d

# 2. WhatsApp connector  (terminal 1)
make install
make connector          # :3001

# 3. Unified API         (terminal 2)
make migrate            # applies EF migrations (also runs automatically on startup)
make api                # :5080

# 4. Test console
open http://localhost:5080   # create account, show QR, watch it connect, send/receive
```

The console at `/` has two panels:

- **Left — Connection.** Create account → **Get QR / connect** → scan with the
  WhatsApp app → status flips to `connected`. Then the *Client API* section unlocks:
  base URL, account id, a webhook subscription (callback URL + one-time signing
  secret), and the list of endpoints a customer would call. **Log out** drops the
  session and wipes stored credentials for a fresh QR.
- **Right — API playground.** Click any endpoint on the left to load it, edit the
  method/path/JSON body, **Send request**, and see status + timing + response.
  A live-messages panel polls `GET /messages` so inbound/outbound traffic shows up.

The account id is remembered in `localStorage`; **Forget account** clears it.

Or drive it by hand with `requests.http`:

1. `POST /api/accounts` `{ "provider": "whatsapp", "password": "secret123" }` → account id
2. Every `/api/accounts/{id}/*` call sends `X-Account-Password: secret123` (or HTTP Basic, user = id)
3. `POST /api/accounts/{id}/webhooks` `{ "callbackUrl": "..." }` → **secret (shown once)**
4. `POST /api/accounts/{id}/connect` → `{ "qrImageDataUrl": "data:image/png;base64,..." }`
5. Render the QR, scan with the WhatsApp app
6. Status flips to `connected` (pushed on the SignalR hub `/hubs/accounts`)
7. Inbound messages → normalized → your `callbackUrl` gets a signed `message.received`
8. `POST /api/accounts/{id}/messages` `{ "chatId": "4917...", "text": "..." }` to reply

### Auth & session lifetime

- The account **password** is set once at creation, stored as a PBKDF2 hash, and
  required on every per-account endpoint. There's no reset — make a new account.
- WhatsApp credentials persist on the connector (`sessions/<id>/`). After one QR
  scan you stay logged in across restarts — **no QR again** — until
  `POST /api/accounts/{id}/logout`, which ends the WhatsApp session and wipes the
  stored credentials so the next connect shows a fresh QR.
- In the console, logout (or "Forget account") clears every panel — messages,
  QR, debug, request/response, history.

---

## The unified webhook

Every customer callback receives the same envelope regardless of channel:

```json
{
  "id": "evt_1f7e830939f447f7b7071fe5dcf9cc4a",
  "type": "message.received",
  "provider": "whatsapp",
  "accountId": "1beaa14e-ca6e-49d0-9d35-99f4a4493a5c",
  "occurredAt": "2026-09-05T14:16:54.5Z",
  "data": {
    "id": "4390e327-9b42-4712-b490-710f3046f960",
    "chatId": "49555000111@s.whatsapp.net",
    "senderId": "49555000111@s.whatsapp.net",
    "text": "Hello",
    "direction": "inbound",
    "timestamp": "2026-09-05T14:20:00Z",
    "attachments": []
  }
}
```

Event types: `message.received`, `message.sent`, `message.reaction_added`,
`message.reaction_removed`, `group.participants_added`, `group.participants_removed`,
`group.participant_promoted`, `group.participant_demoted`, `group.subject_updated`,
`group.description_updated`, `group.joined`, `group.updated`, `poll.vote`,
`account.connected`, `account.needs_reauth`.

`message.*` payloads carry `replyTo { messageId, text, senderId }`, `mentions[]`,
and `poll { name, options }` when present.

Reactions carry `data: { chatId, targetMessageId, emoji, onYourMessage, reactedBy, reactedByMe }`
and are **not** stored as messages — they're delivered as events only.

### Media

Inbound image/video/audio/document/sticker is decrypted by the connector, hosted
at `http://localhost:3001/media/...`, and the URL lands in
`data.attachments[] = { type, url, mimeType, fileName, sizeBytes }`.

Send media by giving `POST /messages` a `media` object instead of `text` — with
either a `url` the connector fetches, or raw bytes as `dataBase64`:

```json
{ "chatId": "4917...", "media": { "type": "image", "url": "https://.../pic.jpg", "caption": "hi" } }
{ "chatId": "4917...", "media": { "type": "document", "dataBase64": "JVBER...", "fileName": "q3.pdf" } }
```

`type` ∈ `image` | `video` | `audio` | `document` (`audio` + `"ptt": true` = voice note).
The console's **Send a message** panel has a file picker that does the base64 for you.

**Send a reaction:**

```
POST /api/accounts/{id}/reactions
{ "chatId": "...", "targetMessageId": "AC96...", "emoji": "👍", "targetFromMe": false }
```

`emoji: ""` removes a reaction. In the console, each message in Live Messages has
a quick-react bar. For a group message reacted to by someone else, also pass
`targetParticipant` (their JID).

**Reply + mention:**

```
POST /api/accounts/{id}/messages
{ "chatId": "...", "text": "@49999 replying", "replyToMessageId": "AC96...",
  "mentions": ["49999@s.whatsapp.net"] }
```

**Poll:**

```
POST /api/accounts/{id}/polls
{ "chatId": "...", "name": "Lunch?", "options": ["Pizza", "Sushi"], "selectableCount": 1 }
```

Votes come back as `poll.vote` webhooks `{ chatId, pollMessageId, voterId, selectedOptions }`
(decrypted + aggregated by the connector, which keeps recent poll messages in memory).

**Groups:**

- `GET /api/accounts/{id}/groups` — list (id, subject, size)
- `GET /api/accounts/{id}/groups/{groupJid}` — full metadata + participants (with admin flags)
- membership/subject changes arrive as `group.*` webhooks

**Conversations (inbox):**

- `GET /api/accounts/{id}/conversations` — one row per chat: name, unread count, last-message preview
- `POST /api/accounts/{id}/conversations/{chatId}/read` — reset unread

Kept fresh automatically as messages flow.

### Verifying the signature

```
X-Unified-Event:     message.received
X-Unified-Delivery:  <delivery id>
X-Unified-Timestamp: 1757082000
X-Unified-Signature: sha256=<hex hmac>
```

`signature = hex( HMAC_SHA256( secret, "{timestamp}.{rawBody}" ) )`

### Delivery guarantees

Each fan-out is persisted as a `WebhookDelivery` row, delivered immediately, and
retried by `WebhookRetryWorker` with exponential backoff + jitter
(~1m → 6m cap, 8 attempts) before being **dead-lettered** (`status = "failed"`).
Inspect with `GET /api/webhooks/{subscriptionId}/deliveries`.

---

## Session durability

Unofficial sessions get logged out / rate-limited / flagged. Handling:

- The connector auto-reconnects (`connection.update` → exponential backoff) unless
  WhatsApp reports `loggedOut`.
- On `loggedOut` it reports `logged_out` → account goes to `needs_reauth` and an
  `account.needs_reauth` webhook fires so the customer UI can prompt a rescan.
- `AccountHealthWorker` escalates accounts stuck `disconnected` past a grace window.

---

## Adding the next provider

Implement `IMessagingProviderAdapter`, register it in `Program.cs`:

```csharp
builder.Services.AddScoped<IMessagingProviderAdapter, TelegramAdapter>();
```

`ProviderResolver` picks it up by `ProviderName`. Inbound webhooks route by URL
segment: `POST /api/webhooks/{provider}`. Nothing else changes — the unified
model, dispatcher, retry and signing are provider-agnostic.

| Channel | Method | Difficulty |
|---------|--------|-----------|
| Telegram | Official Bot API, real webhooks | Easy |
| Gmail / Outlook | OAuth2 | Easy |
| LinkedIn | Unofficial, browser session | Hard (same fragility as WhatsApp) — **implemented**, see below |

### LinkedIn (second channel — unofficial)

`linkedin-connector/` + `LinkedInAdapter` connect a **personal LinkedIn account**
the "WhatsApp way": no official API, just the logged-in session (the Voyager
approach Unipile uses). There is **no QR**. See [`LINKEDIN_PLAN.md`](LINKEDIN_PLAN.md)
for the design + ban-avoidance rules, and
[`linkedin-connector/README.md`](linkedin-connector/README.md) to run it.

```bash
make install            # installs both connectors
make linkedin-connector # :3002

# then, against the unified API:
POST /api/accounts               { "provider": "linkedin", "password": "..." }
POST /api/accounts/{id}/connect  { "cookieHeader": "li_at=...; JSESSIONID=\"ajax:...\"; ...",
                                   "proxyUrl": "http://user:pass@host:port" }
# status -> connected; inbound DMs -> signed message.received; POST /messages to reply
```

Auth options on `/connect`: full cookie jar (`cookieHeader`), individual fields
(`li_at` + `jsessionid`), or `username` + `password` (needs Playwright in the
connector; checkpoint code goes back via `{ "challengeCode": "..." }`).

**Ban-avoidance built in** (§6 of the plan): per-account proxy (`PROXY_PROVIDER` =
`manual` paste / `webshare` auto-provision / `none`), persisted browser
fingerprint, full cookie jar with `Set-Cookie` refresh, and a per-account
scheduler enforcing rolling-24h caps (send/invite/read/search), burst limits,
human gap+jitter, quiet hours, and a `429`/`999` cooldown.

`chatId` is a LinkedIn thread id (`2-…==`) or a member URN. Media, reactions,
group threads and fleet orchestration are phase 2/3.

---

## Configuration

**Backend** (`appsettings.json` / env):

| Key | Default |
|-----|---------|
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Database=unified_messaging;Username=unified;Password=unified` |
| `WhatsAppConnector__BaseUrl` | `http://localhost:3001` |
| `LinkedInConnector__BaseUrl` | `http://localhost:3002` |
| `Connector__SharedSecret` | `dev-connector-secret` |

**Connector** (`.env`, see `.env.example`): `PORT`, `BACKEND_URL`,
`CONNECTOR_SECRET`, `SESSIONS_DIR`.

The shared secret gates both directions (`X-Connector-Secret` header).
