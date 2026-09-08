"use strict";

const express = require("express");
const pino = require("pino");

const config = require("./config");
const { SessionManager } = require("./sessionManager");

const logger = pino({
  level: process.env.LOG_LEVEL ?? "info",
  transport: process.env.NODE_ENV === "production" ? undefined : { target: "pino-pretty" },
});

const app = express();
app.use(express.json({ limit: "2mb" }));

const sessions = new SessionManager(logger);

// ---- Shared-secret gate for everything the .NET backend calls ----
app.use((req, res, next) => {
  if (req.path === "/health") return next();
  if (req.get("X-Connector-Secret") !== config.connectorSecret) {
    return res.status(401).json({ error: "bad connector secret" });
  }
  next();
});

app.get("/health", (_req, res) => res.json({ status: "ok", sessions: sessions.sessions.size }));

// Debug: recent activity per account.
app.get("/debug/events", (_req, res) => res.json(sessions.debug));

// Debug: per-account session state.
app.get("/sessions", (_req, res) => {
  const out = {};
  for (const [id, s] of sessions.sessions) {
    out[id] = {
      status: s.status,
      memberUrn: s.memberUrn ?? null,
      proxied: !!s.identity.proxyUrl,
      scheduler: s.sched.snapshot(),
      threads: s.lastActivity.size,
      realtime: !!s.realtime,
    };
  }
  for (const id of sessions.pendingLogins.keys()) {
    out[id] = { status: "waiting_for_credentials", checkpoint: true };
  }
  res.json(out);
});

// Begin (or resume) a session. Body (all optional except a cookie/login on first connect):
//   { li_at, jsessionid } | { cookieHeader } | { cookies:[...] } | { username, password }
//   plus { proxyUrl?, userAgent?, acceptLang?, timezone?, country? }
app.post("/accounts/:id/connect", async (req, res, next) => {
  try {
    const snap = await sessions.start(req.params.id, req.body ?? {});
    res.json(snap);
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// Submit an emailed / SMS / authenticator code for a pending login checkpoint.
app.post("/accounts/:id/challenge", async (req, res, next) => {
  try {
    const { code } = req.body ?? {};
    if (!code) return res.status(400).json({ error: "code is required" });
    const snap = await sessions.submitChallenge(req.params.id, code);
    res.json(snap);
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// Poll status (kept named /qr for parity with the WhatsApp connector; qr is always null).
app.get("/accounts/:id/qr", (req, res) => {
  res.json(sessions.snapshot(req.params.id));
});

// Send a text message: { to, text }  (to = threadId "2-..==" or member URN / id).
app.post("/accounts/:id/send", async (req, res, next) => {
  try {
    const { to, text } = req.body ?? {};
    const result = await sessions.send(req.params.id, to, text);
    res.json({ status: "sent", ...result });
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// Force logout (wipes stored cookie + identity).
app.post("/accounts/:id/logout", async (req, res, next) => {
  try {
    await sessions.logout(req.params.id);
    res.json({ status: "logged_out" });
  } catch (err) {
    next(err);
  }
});

// eslint-disable-next-line no-unused-vars
app.use((err, _req, res, _next) => {
  logger.error({ err: err.message, stack: err.stack }, "request failed");
  res.status(500).json({ error: "internal error" });
});

app.listen(config.port, () => {
  logger.info(
    {
      port: config.port,
      backendUrl: config.backendUrl,
      sessionsDir: config.sessionsDir,
      proxyProvider: config.proxyProvider,
      realtime: config.realtimeEnabled,
      requireProxy: config.requireProxy,
      pollIntervalMs: config.pollIntervalMs,
      dailyCaps: config.dailyCaps,
      login: config.loginEnabled ? "enabled" : "disabled",
      secretSet: config.connectorSecret !== "dev-connector-secret" ? "custom" : "default",
    },
    "linkedin-connector listening",
  );
});
