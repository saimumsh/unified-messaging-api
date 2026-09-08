"use strict";

const fs = require("fs");
const path = require("path");

const config = require("./config");
const { reportStatus, forwardMessage } = require("./backend");
const { Identity } = require("./identity");
const { Scheduler } = require("./scheduler");
const { parseJar } = require("./cookies");
const { resolveProxy } = require("./proxy");
const { LoginAttempt } = require("./login");
const { VoyagerClient, normalizeMemberUrn } = require("./voyager");
const { RealtimeStream } = require("./realtime");

/**
 * Owns every LinkedIn session, one per accountId. Mirrors the WhatsApp
 * connector's SessionManager. Receive path = conversation poller (reliable) +
 * optional realtime stream (low latency, best effort).
 */
class SessionManager {
  constructor(logger) {
    this.logger = logger;
    /** @type {Map<string, Session>} */
    this.sessions = new Map();
    /** accountId -> LoginAttempt paused on a checkpoint. */
    this.pendingLogins = new Map();
    /** Ring buffer of recent activity, for GET /debug/events. */
    this.debug = [];
  }

  _debug(entry) {
    this.debug.unshift({ at: new Date().toISOString(), ...entry });
    if (this.debug.length > 50) this.debug.pop();
  }

  snapshot(accountId) {
    const s = this.sessions.get(accountId);
    if (this.pendingLogins.has(accountId)) {
      return { status: "waiting_for_credentials", qr: null, externalAccountId: null, checkpoint: true };
    }
    if (!s) {
      // A persisted-but-not-loaded session still counts as "known".
      return fs.existsSync(cookieFile(accountId))
        ? { status: "disconnected", qr: null, externalAccountId: null }
        : { status: "waiting_for_credentials", qr: null, externalAccountId: null };
    }
    return {
      status: s.status,
      qr: null,
      externalAccountId: s.memberUrn ?? null,
      scheduler: s.sched.snapshot(),
    };
  }

  /**
   * Connect. Credential shapes accepted:
   *   { li_at, jsessionid, ... }              cookie fields
   *   { cookieHeader } | { cookies:[...] }    full jar (preferred)
   *   { username, password }                 browser-assisted login (Playwright)
   *   {} / undefined                         resume from disk
   * plus { proxyUrl?, userAgent?, acceptLang?, timezone?, country? }.
   */
  async start(accountId, credentials = {}) {
    const log = this.logger.child({ accountId });

    let identity = Identity.load(accountId);
    let jar = null;

    const hasCookieInput =
      credentials.li_at || credentials.cookieHeader || credentials.cookie || Array.isArray(credentials.cookies);
    const hasPasswordInput = credentials.username && credentials.password;

    // Build / refresh the per-account identity (fingerprint + proxy).
    if (hasCookieInput || hasPasswordInput || !identity) {
      identity = new Identity(accountId, {
        proxyUrl: identity?.proxyUrl,
        userAgent: credentials.userAgent || identity?.userAgent,
        acceptLang: credentials.acceptLang || identity?.acceptLang,
        deviceId: identity?.deviceId,
        timezone: credentials.timezone || identity?.timezone,
      });
      let proxyUrl;
      try {
        proxyUrl = await resolveProxy(
          {
            accountId,
            suppliedProxyUrl: credentials.proxyUrl,
            storedProxyUrl: identity.proxyUrl,
            country: credentials.country,
          },
          log,
        );
      } catch (err) {
        if (!identity.proxyUrl && !hasCookieInput && !hasPasswordInput) {
          reportStatus(accountId, "waiting_for_credentials", {}, log);
          return this.snapshot(accountId);
        }
        throw err;
      }
      identity.setProxyUrl(proxyUrl);
      identity.save();
    }

    // ---- obtain a cookie jar ----
    if (hasPasswordInput) {
      const attempt = new LoginAttempt(accountId, identity, log);
      this.pendingLogins.set(accountId, attempt);
      const out = await attempt.start(credentials.username, credentials.password);
      if (out.status === "checkpoint") {
        this._debug({ accountId, checkpoint: true });
        reportStatus(accountId, "waiting_for_credentials", {}, log);
        return this.snapshot(accountId);
      }
      jar = out.jar;
      this.pendingLogins.delete(accountId);
      writeJar(accountId, jar);
    } else if (hasCookieInput) {
      jar = parseJar(credentials);
      writeJar(accountId, jar);
    } else {
      const stored = readJar(accountId);
      if (stored) jar = parseJar({ cookies: mapToPairs(stored) });
    }

    if (!jar || !identity) {
      reportStatus(accountId, "waiting_for_credentials", {}, log);
      return this.snapshot(accountId);
    }

    return this._bringUp(accountId, jar, identity, log);
  }

  /** Submit an emailed / SMS / authenticator code for a pending login checkpoint. */
  async submitChallenge(accountId, code) {
    const attempt = this.pendingLogins.get(accountId);
    if (!attempt) throw conflict("no checkpoint pending for this account");
    const log = this.logger.child({ accountId });
    const out = await attempt.submitChallenge(String(code || "").trim());
    if (out.status === "checkpoint") return this.snapshot(accountId); // another step
    this.pendingLogins.delete(accountId);
    writeJar(accountId, out.jar);
    return this._bringUp(accountId, out.jar, attempt.identity, log);
  }

  /** Validate the jar, wire the Voyager client, start receivers. */
  async _bringUp(accountId, jar, identity, log) {
    await this._teardown(accountId);

    const session = new Session(accountId, identity);
    session.jar = jar;
    session.voyager = new VoyagerClient(jar, identity, log, (fresh) => {
      // LinkedIn rotated a cookie mid-session — persist so a restart stays logged in.
      try {
        writeJar(accountId, jar.toJSON());
        this._debug({ accountId, cookieRefresh: Object.keys(fresh) });
      } catch {
        /* best effort */
      }
    });
    this.sessions.set(accountId, session);
    session.status = "connecting";

    try {
      const memberUrn = await session.voyager.me();
      session.memberUrn = memberUrn;
      session.status = "connected";
      log.info({ memberUrn, proxy: !!identity.proxyUrl }, "linkedin session connected");
      this._debug({ accountId, connected: true, memberUrn });
      reportStatus(accountId, "connected", { externalAccountId: memberUrn }, log);
    } catch (err) {
      return this._onError(accountId, err, log, { duringConnect: true });
    }

    this._startPoller(accountId);
    if (config.realtimeEnabled) this._startRealtime(accountId);
    return this.snapshot(accountId);
  }

  async send(accountId, to, text) {
    const session = this.sessions.get(accountId);
    if (!session || session.status !== "connected") {
      throw conflict("account not connected");
    }
    if (!to) throw badRequest("to is required");
    if (!text) throw badRequest("text is required");

    const isThread = !to.startsWith("urn:") && !to.includes("@") && to.includes("-");

    // New 1:1 threads count as an "invite"-class action (stricter cap); replies
    // into an existing thread are "send".
    await session.sched.schedule(isThread ? "send" : "invite");

    const result = isThread
      ? await withErrorTag(() => session.voyager.sendToThread(to, text), session)
      : await withErrorTag(() => session.voyager.sendToMember(normalizeMemberUrn(to), text), session);

    // Remember our own outbound event id so the poll/realtime echo dedupes.
    if (result.id) session.seen.add(result.id);
    this._debug({ accountId, sent: true, to, eventUrn: result.id ?? null });
    return result;
  }

  async logout(accountId) {
    const log = this.logger.child({ accountId });
    this.pendingLogins.get(accountId)?.close?.();
    this.pendingLogins.delete(accountId);
    await this._teardown(accountId);
    fs.rmSync(path.join(config.sessionsDir, accountId), { recursive: true, force: true });
    log.info("linkedin session logged out and wiped");
    reportStatus(accountId, "disconnected", {}, log);
  }

  // ---- receive: conversation poller ----

  _startPoller(accountId) {
    const session = this.sessions.get(accountId);
    if (!session) return;
    const log = this.logger.child({ accountId });

    const tick = async () => {
      if (!this.sessions.has(accountId)) return;
      try {
        if (!session.sched.inCooldown) await this._pollOnce(accountId, log);
      } catch (err) {
        this._onError(accountId, err, log, {});
      } finally {
        if (this.sessions.has(accountId)) {
          const jitter = Math.floor(Math.random() * 20_000) - 10_000;
          session.pollTimer = setTimeout(tick, Math.max(30_000, config.pollIntervalMs + jitter));
        }
      }
    };
    session.pollTimer = setTimeout(tick, 2000);
  }

  async _pollOnce(accountId, log) {
    const session = this.sessions.get(accountId);
    if (!session) return;

    // The passive sync poll is not metered against the daily read cap (it would
    // exhaust it on its own) — only its own pace + the cooldown gate it.
    const convData = await session.voyager.listConversations({ limit: 20 });
    const convos = extractConversations(convData);

    for (const c of convos) {
      if (!c.threadId) continue;
      if (session.lastActivity.get(c.threadId) === c.lastActivityAt) continue;
      session.lastActivity.set(c.threadId, c.lastActivityAt);

      // Fetching a thread's events *is* metered — bounded by real message volume.
      try {
        await session.sched.schedule("read", { outbound: false });
      } catch {
        break; // daily read cap hit — pick the rest up next tick
      }
      const evData = await session.voyager.conversationEvents(c.threadId, { limit: 20 });
      const events = extractEvents(evData, c.threadId);
      for (const ev of events) this._forwardEvent(accountId, ev, log);
    }
  }

  _forwardEvent(accountId, ev, log) {
    const session = this.sessions.get(accountId);
    if (!session || !ev.eventUrn) return;
    if (session.seen.has(ev.eventUrn)) return;
    session.seen.add(ev.eventUrn);
    if (session.seen.size > 2000) session.seen.delete(session.seen.values().next().value);

    const fromMe = !!session.memberUrn && ev.from === session.memberUrn;
    const raw = {
      threadId: ev.threadId,
      eventUrn: ev.eventUrn,
      from: ev.from ?? null,
      fromMe,
      subject: ev.subject ?? null,
      body: { text: ev.text ?? "" },
      attachments: ev.attachments ?? [],
      createdAt: ev.createdAt ?? Date.now(),
    };
    this._debug({ accountId, forwarded: true, eventUrn: ev.eventUrn, fromMe });
    forwardMessage(accountId, raw, log);
  }

  // ---- receive: realtime stream (best effort) ----

  _startRealtime(accountId) {
    const session = this.sessions.get(accountId);
    if (!session) return;
    const log = this.logger.child({ accountId, comp: "realtime" });

    const stream = new RealtimeStream(session.voyager, log);
    session.realtime = stream;

    stream.on("event", (decorated) => {
      try {
        const ev = extractRealtimeEvent(decorated);
        if (ev) this._forwardEvent(accountId, ev, log);
      } catch (err) {
        log.warn({ err: err.message }, "realtime event parse failed");
      }
    });
    stream.on("challenge", (err) => this._onError(accountId, tag(err, "challenge"), log, {}));
    stream.on("error", (err) => {
      log.warn({ err: err.message }, "realtime stream error — falling back to poller, will retry");
      if (err.kind === "ratelimit") session.sched.cooldown();
      this._scheduleRealtimeReconnect(accountId);
    });

    stream.start().catch((err) => log.warn({ err: err.message }, "realtime start failed"));
  }

  _scheduleRealtimeReconnect(accountId) {
    const session = this.sessions.get(accountId);
    if (!session) return;
    session.realtimeAttempts = (session.realtimeAttempts ?? 0) + 1;
    const delay = Math.min(
      config.reconnectBaseDelay * 2 ** (session.realtimeAttempts - 1),
      config.reconnectMaxDelay,
    );
    clearTimeout(session.realtimeTimer);
    session.realtimeTimer = setTimeout(() => {
      if (this.sessions.get(accountId)?.status === "connected") this._startRealtime(accountId);
    }, delay);
  }

  // ---- error handling (§6.4) ----

  _onError(accountId, err, log, { duringConnect }) {
    const session = this.sessions.get(accountId);
    const kind = err.kind || (err.statusCode === 429 ? "ratelimit" : "http");

    if (kind === "challenge" || kind === "forbidden") {
      log.warn({ err: err.message }, "linkedin challenge / session invalid — needs reauth");
      this._debug({ accountId, needsReauth: true, err: err.message });
      if (session) session.status = "logged_out";
      this._teardown(accountId);
      // Drop the (now invalid / server-cleared) cookie jar so the next connect
      // asks for a fresh one; keep identity.json + usage.json.
      try {
        fs.rmSync(cookieFile(accountId), { force: true });
      } catch {
        /* ignore */
      }
      reportStatus(accountId, "logged_out", {}, log);
      return { status: "logged_out", qr: null, externalAccountId: null };
    }

    if (kind === "ratelimit") {
      log.warn("linkedin rate limit — entering cooldown");
      this._debug({ accountId, rateLimited: true });
      session?.sched.cooldown();
      if (session && session.status !== "connected") session.status = "connected"; // stay, just throttled
      return this.snapshot(accountId);
    }

    log.error({ err: err.message }, "linkedin session error");
    this._debug({ accountId, error: err.message });
    if (duringConnect) {
      if (session) session.status = "disconnected";
      this._teardown(accountId);
      reportStatus(accountId, "disconnected", {}, log);
    }
    return this.snapshot(accountId);
  }

  async _teardown(accountId) {
    const session = this.sessions.get(accountId);
    if (!session) return;
    clearTimeout(session.pollTimer);
    clearTimeout(session.realtimeTimer);
    try {
      session.realtime?.stop();
    } catch {
      /* ignore */
    }
    await session.identity.close();
    this.sessions.delete(accountId);
  }
}

// ---- Session record ----

class Session {
  constructor(accountId, identity) {
    this.accountId = accountId;
    this.identity = identity;
    this.voyager = null;
    this.jar = null;
    this.sched = new Scheduler(accountId, identity.timezone);
    this.status = "connecting";
    this.memberUrn = null;
    this.seen = new Set();
    this.lastActivity = new Map(); // threadId -> lastActivityAt
    this.pollTimer = null;
    this.realtime = null;
    this.realtimeTimer = null;
    this.realtimeAttempts = 0;
  }
}

// ---- cookie-jar persistence (full jar, not just li_at) ----

function cookieFile(accountId) {
  return path.join(config.sessionsDir, accountId, "cookie.json");
}
function readJar(accountId) {
  try {
    return JSON.parse(fs.readFileSync(cookieFile(accountId), "utf8"));
  } catch {
    return null;
  }
}
function writeJar(accountId, jarOrMap) {
  const map = typeof jarOrMap.toJSON === "function" ? jarOrMap.toJSON() : jarOrMap;
  const dir = path.join(config.sessionsDir, accountId);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(cookieFile(accountId), JSON.stringify(map));
}
function mapToPairs(map) {
  return Object.entries(map).map(([name, value]) => ({ name, value }));
}

// ---- normalization helpers (defensive — Voyager shapes drift) ----

/** Pull a flat list of { threadId, lastActivityAt } from a conversations response. */
function extractConversations(data) {
  const out = [];
  const seen = new Set();
  const push = (threadId, lastActivityAt) => {
    if (!threadId || seen.has(threadId)) return;
    seen.add(threadId);
    out.push({ threadId, lastActivityAt: lastActivityAt ?? 0 });
  };

  const scan = (arr) => {
    for (const el of arr ?? []) {
      if (!el || typeof el !== "object") continue;
      const urn = el.entityUrn || el.dashEntityUrn || "";
      const idFromUrn = /conversation:\(?.*?([0-9]-[A-Za-z0-9+/=]+==?)/.exec(urn)?.[1];
      const threadId =
        el.threadId ||
        idFromUrn ||
        (typeof el.conversationUrn === "string" ? el.conversationUrn.split(":").pop() : null);
      const last =
        el.lastActivityAt ||
        el.lastMessageAt ||
        el.events?.[0]?.createdAt ||
        el.conversation?.lastActivityAt;
      if (threadId) push(threadId, last);
    }
  };

  scan(data?.elements);
  scan(data?.data?.elements);
  scan(data?.included);
  return out;
}

/** Pull normalized events from a conversation-events response. */
function extractEvents(data, threadId) {
  const candidates = [
    ...(data?.elements ?? []),
    ...(data?.data?.elements ?? []),
    ...(data?.included ?? []),
  ];
  const out = [];
  for (const el of candidates) {
    const ev = shapeEvent(el, threadId);
    if (ev?.eventUrn && (ev.text || (ev.attachments && ev.attachments.length))) out.push(ev);
  }
  return out;
}

function shapeEvent(el, threadId) {
  if (!el || typeof el !== "object") return null;
  const type = el.$type || "";
  const isEvent =
    /messaging\.(Event|MessagingMessage)/i.test(type) ||
    /msg_event/.test(el.entityUrn || "") ||
    !!el.eventContent;

  if (!isEvent) return null;

  const eventUrn = el.entityUrn || el.dashEntityUrn || null;
  const content =
    el.eventContent?.["com.linkedin.voyager.messaging.event.MessageEvent"] ||
    el.eventContent ||
    el;

  const text =
    content?.attributedBody?.text ??
    content?.body ??
    (typeof content?.subject === "string" ? content.subject : null) ??
    null;

  const from =
    el["*from"] ||
    el.from?.["com.linkedin.voyager.messaging.MessagingMember"]?.["*miniProfile"] ||
    el.from?.entityUrn ||
    (typeof el.from === "string" ? el.from : null);

  const attachments = [];
  for (const a of content?.attachments ?? []) {
    attachments.push({
      type: guessType(a?.mediaType || a?.contentType),
      url: a?.reference?.string || a?.reference || a?.url || null,
      mimeType: a?.mediaType || a?.contentType || null,
      fileName: a?.name || null,
      size: a?.byteSize || a?.size || null,
    });
  }
  for (const m of content?.customContent?.mediaAttachments ?? []) {
    attachments.push({ type: "image", url: m?.url || null, mimeType: null, fileName: null, size: null });
  }

  return {
    threadId:
      threadId ||
      /conversation:\(?.*?([0-9]-[A-Za-z0-9+/=]+==?)/.exec(eventUrn || "")?.[1] ||
      null,
    eventUrn,
    from: from ? normalizeMemberUrn(from) : null,
    text,
    subject: typeof content?.subject === "string" ? content.subject : null,
    attachments,
    createdAt: el.createdAt || content?.createdAt || Date.now(),
  };
}

function extractRealtimeEvent(decorated) {
  const payload = decorated?.payload || decorated;
  const evt =
    payload?.event ||
    payload?.["com.linkedin.voyager.messaging.event.MessageEvent"] ||
    payload;
  if (!evt) return null;
  return shapeEvent(evt, null);
}

function guessType(mime) {
  if (!mime) return "document";
  if (mime.startsWith("image/")) return "image";
  if (mime.startsWith("video/")) return "video";
  if (mime.startsWith("audio/")) return "audio";
  return "document";
}

// ---- small error helpers ----

function badRequest(msg) {
  const e = new Error(msg);
  e.statusCode = 400;
  return e;
}
function conflict(msg) {
  const e = new Error(msg);
  e.statusCode = 409;
  return e;
}
function tag(err, kind) {
  err.kind = kind;
  return err;
}
async function withErrorTag(fn, session) {
  try {
    return await fn();
  } catch (err) {
    if (err.kind === "ratelimit") {
      err.statusCode = 429;
      session?.sched.cooldown();
    }
    if (err.kind === "challenge" || err.kind === "forbidden") err.statusCode = 401;
    throw err;
  }
}

module.exports = { SessionManager };
