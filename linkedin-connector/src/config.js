"use strict";

const bool = (v, def) => (v == null ? def : /^(1|true|yes)$/i.test(String(v)));
const int = (v, def) => (v == null || v === "" ? def : parseInt(v, 10));

module.exports = {
  port: int(process.env.PORT, 3002),

  // .NET backend base URL and the shared secret both sides check.
  backendUrl: process.env.BACKEND_URL ?? "http://localhost:5080",
  connectorSecret: process.env.CONNECTOR_SECRET ?? "dev-connector-secret",

  // Where per-account cookie jar + identity + usage are stored (sessions/<id>/).
  sessionsDir: process.env.SESSIONS_DIR ?? "./sessions",

  // ---- Ban-avoidance (§6 of LINKEDIN_PLAN.md) ----
  defaultUserAgent:
    process.env.DEFAULT_USER_AGENT ??
    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
      "(KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36",
  requireProxy: bool(process.env.REQUIRE_PROXY, true),

  // ---- Managed proxy layer (proxy.js) ----
  // manual  = use the proxyUrl supplied on /connect
  // webshare = auto-provision a sticky residential endpoint from Webshare's API
  // none    = no proxy (dev only)
  proxyProvider: (process.env.PROXY_PROVIDER ?? "manual").toLowerCase(),
  proxyDefaultCountry: (process.env.PROXY_DEFAULT_COUNTRY ?? "").toUpperCase() || null,
  webshareApiKey: process.env.WEBSHARE_API_KEY ?? null,
  // Optional plain "host:port:user:pass" rotating endpoint some providers hand out.
  proxyRotatingEndpoint: process.env.PROXY_ROTATING_ENDPOINT ?? null,

  // ---- Rate-limit scheduler (scheduler.js) ----
  // Rolling-24h caps per account, LinkedIn-safe defaults.
  dailyCaps: {
    send: int(process.env.CAP_SEND_PER_DAY, 100),
    invite: int(process.env.CAP_INVITE_PER_DAY, 25),
    read: int(process.env.CAP_READ_PER_DAY, 400),
    search: int(process.env.CAP_SEARCH_PER_DAY, 40),
  },
  // Short-window burst guards.
  rlSendsPerHour: int(process.env.RL_SENDS_PER_HOUR, 20),
  rlReadsPerMin: int(process.env.RL_READS_PER_MIN, 10),
  // Minimum gap between actions (ms) + random extra jitter up to this much.
  minActionGapMs: int(process.env.MIN_ACTION_GAP_MS, 4000),
  actionJitterMs: int(process.env.ACTION_JITTER_MS, 9000),
  // "22-7" => pause outbound actions 22:00–07:00 in the account's timezone.
  quietHours: process.env.QUIET_HOURS ?? null,

  // Conversation-poll backstop (with jitter). Also the primary receive path when
  // REALTIME_ENABLED is false or the SSE stream is down.
  pollIntervalMs: int(process.env.POLL_INTERVAL_MS, 90000),
  realtimeEnabled: bool(process.env.REALTIME_ENABLED, true),

  // Auto-reconnect backoff (ms) for the realtime stream.
  reconnectBaseDelay: 3000,
  reconnectMaxDelay: 120000,

  // Cooldown after a 429 / LinkedIn 999.
  rateLimitCooldownMs: 5 * 60 * 1000,

  // ---- Browser-assisted login (login.js, optional — needs `npm i playwright`) ----
  loginEnabled: bool(process.env.LOGIN_ENABLED, true),
  loginHeadless: bool(process.env.LOGIN_HEADLESS, true),

  linkedInBase: "https://www.linkedin.com",
  voyagerBase: "https://www.linkedin.com/voyager/api",
};
