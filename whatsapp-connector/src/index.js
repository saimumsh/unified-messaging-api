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
app.use(express.json({ limit: "5mb" }));

const sessions = new SessionManager(logger);

// Decrypted inbound media, served by unguessable path (accountId/messageId.ext).
// Public so webhook consumers can fetch the URL we hand them.
app.use("/media", express.static(config.mediaDir));

// ---- Shared-secret gate for everything the .NET backend calls ----
app.use((req, res, next) => {
  if (req.path === "/health" || req.path.startsWith("/media/")) return next();
  if (req.get("X-Connector-Secret") !== config.connectorSecret) {
    return res.status(401).json({ error: "bad connector secret" });
  }
  next();
});

app.get("/health", (_req, res) => res.json({ status: "ok", sessions: sessions.sessions.size }));

// Debug: the last ~50 inbound events and what the connector did with each.
app.get("/debug/events", (_req, res) => res.json(sessions.debug));

// Debug: per-account session state (status, whether a live socket exists).
app.get("/sessions", (_req, res) => {
  const out = {};
  for (const [id, s] of sessions.sessions) {
    out[id] = { status: s.status, hasSocket: !!s.sock, hasQr: !!s.qr, reconnectAttempts: s.reconnectAttempts ?? 0 };
  }
  res.json(out);
});

// Begin (or resume) a session.
app.post("/accounts/:id/connect", async (req, res, next) => {
  try {
    const snap = await sessions.start(req.params.id);
    res.json(snap);
  } catch (err) {
    next(err);
  }
});

// Poll the QR / status.
app.get("/accounts/:id/qr", (req, res) => {
  res.json(sessions.snapshot(req.params.id));
});

// Send: { to, text, replyTo?, mentions? } or { to, media: {...} }.
app.post("/accounts/:id/send", async (req, res, next) => {
  try {
    const { to, text, media, replyTo, mentions } = req.body ?? {};
    if (!to) return res.status(400).json({ error: "to is required" });

    let result;
    if (media?.url || media?.dataBase64) {
      result = await sessions.sendMedia(req.params.id, to, media);
    } else if (text) {
      result = await sessions.send(req.params.id, to, text, { replyTo, mentions });
    } else {
      return res.status(400).json({ error: "text, media.url or media.dataBase64 is required" });
    }
    res.json({ status: "sent", ...result });
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// Create a poll: { to, poll: { name, options: [string], selectableCount? } }.
app.post("/accounts/:id/poll", async (req, res, next) => {
  try {
    const { to, poll } = req.body ?? {};
    if (!to || !poll?.name || !(poll.options?.length >= 2)) {
      return res.status(400).json({ error: "to, poll.name and >= 2 poll.options are required" });
    }
    const result = await sessions.sendPoll(req.params.id, to, poll);
    res.json({ status: "sent", ...result });
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// Full metadata + participants for one group.
app.get("/accounts/:id/groups/:jid", async (req, res, next) => {
  try {
    res.json(await sessions.groupMetadata(req.params.id, req.params.jid));
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// React to a message: { to, target: { id, fromMe?, participant? }, emoji }  ("" removes).
app.post("/accounts/:id/react", async (req, res, next) => {
  try {
    const { to, target, emoji } = req.body ?? {};
    if (!to || !target?.id) return res.status(400).json({ error: "to and target.id are required" });
    const result = await sessions.react(req.params.id, to, target, emoji);
    res.json({ status: "sent", ...result });
  } catch (err) {
    if (err.statusCode) return res.status(err.statusCode).json({ error: err.message });
    next(err);
  }
});

// List groups the account participates in.
app.get("/accounts/:id/groups", async (req, res, next) => {
  try {
    res.json(await sessions.groups(req.params.id));
  } catch (err) {
    if (err.statusCode === 409) return res.status(409).json({ error: err.message });
    next(err);
  }
});

// Force logout (invalidates stored credentials).
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
      publicUrl: config.publicUrl,
      mediaDir: config.mediaDir,
      secretSet: config.connectorSecret !== "dev-connector-secret" ? "custom" : "default",
    },
    "whatsapp-connector listening",
  );
});
