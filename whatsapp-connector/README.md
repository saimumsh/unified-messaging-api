# whatsapp-connector

Node + [Baileys](https://github.com/WhiskeySockets/Baileys) service that owns the
raw WhatsApp multi-device sockets, one per account, for the Unified Messaging API.

```bash
npm install
cp .env.example .env
npm start        # :3001
```

## Endpoints (called by the .NET backend, gated by `X-Connector-Secret`)

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/accounts/:id/connect` | Start/resume a session → `{ status, qr }` |
| `GET`  | `/accounts/:id/qr` | Poll `{ status, qr }` |
| `POST` | `/accounts/:id/send` | `{ to, text }` or `{ to, media }` → send (`to` = number or `<jid>@g.us`) |
| `POST` | `/accounts/:id/send` | also accepts `replyTo` (message id) and `mentions` (jids) |
| `POST` | `/accounts/:id/react` | `{ to, target: { id, fromMe?, participant? }, emoji }` — `emoji: ""` removes |
| `POST` | `/accounts/:id/poll` | `{ to, poll: { name, options, selectableCount } }` |
| `GET`  | `/accounts/:id/groups` | Groups the account participates in → `[{ id, subject, size }]` |
| `GET`  | `/accounts/:id/groups/:jid` | Full metadata + participants (admin flags) |
| `GET`  | `/media/:accountId/:file` | Decrypted inbound media (no auth) |
| `POST` | `/accounts/:id/logout` | Invalidate stored credentials |
| `GET`  | `/health` | — |

## Calls it makes to the backend

- `POST /api/internal/whatsapp/status` — `connected` / `waiting_for_scan` / `disconnected` / `logged_out`
- `POST /api/webhooks/whatsapp` — `{ accountId, raw }` for `messages.upsert`;
  `{ accountId, raw: { reactionEvent } }` for reactions;
  `{ accountId, raw: { groupEvent } }` for `group-participants.update` / `groups.update` / `groups.upsert`;
  `{ accountId, raw: { pollVote } }` for decrypted poll votes (`messages.update`)

## Notes

- Auth state per account lives in `sessions/<accountId>/` (multi-file). **Not** committed.
- Auto-reconnects on unexpected disconnects with exponential backoff; stops on `loggedOut`.
- Media: inbound image/video/audio/document/sticker is decrypted with
  `downloadMediaMessage`, written to `media/<accountId>/<msgId>.<ext>`, and served
  at `${CONNECTOR_PUBLIC_URL}/media/...` (unguarded path, unguessable by id). That
  URL is included in the forwarded payload as `media`. Swap the local dir for blob
  storage in `_downloadMedia` for production.
- Outbound media: `POST /accounts/:id/send` also accepts
  `{ to, media: { type, url, caption?, fileName?, mimetype?, ptt? } }` — the
  connector fetches `url` and sends it via Baileys.
