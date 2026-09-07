"use strict";

const fs = require("fs");
const path = require("path");
const { Boom } = require("@hapi/boom");
const QRCode = require("qrcode");
const {
  default: makeWASocket,
  useMultiFileAuthState,
  fetchLatestBaileysVersion,
  downloadMediaMessage,
  getAggregateVotesInPollMessage,
  jidNormalizedUser,
  DisconnectReason,
} = require("@whiskeysockets/baileys");

const config = require("./config");
const { reportStatus, forwardMessage } = require("./backend");

// WhatsApp wraps real content in these container nodes; peel them off.
const CONTAINER_KEYS = [
  "ephemeralMessage",
  "viewOnceMessage",
  "viewOnceMessageV2",
  "viewOnceMessageV2Extension",
  "documentWithCaptionMessage",
  "deviceSentMessage",
  "editedMessage",
];

function unwrapMessage(message, depth = 0) {
  if (!message || depth > 5) return message;
  for (const k of CONTAINER_KEYS) {
    if (message[k]?.message) return unwrapMessage(message[k].message, depth + 1);
  }
  return message;
}

/**
 * Owns every live WhatsApp socket, one per accountId. The rest of the service
 * only talks to this class.
 */
class SessionManager {
  constructor(logger) {
    this.logger = logger;
    /** @type {Map<string, { sock: any, qr: string|null, status: string, reconnectAttempts: number }>} */
    this.sessions = new Map();
    /** Ring buffer of recent inbound events, for GET /debug/events. */
    this.debug = [];
    /** accountId -> Map(messageId -> full WAMessage), for quoted replies. Bounded. */
    this._recent = new Map();
    /** accountId -> Map(pollMessageId -> poll creation WAMessage), for vote aggregation. */
    this._polls = new Map();
  }

  _remember(accountId, msg) {
    if (!msg?.key?.id) return;
    let m = this._recent.get(accountId);
    if (!m) this._recent.set(accountId, (m = new Map()));
    m.set(msg.key.id, msg);
    if (m.size > 300) m.delete(m.keys().next().value); // FIFO cap

    const content = msg.message ?? {};
    if (content.pollCreationMessage || content.pollCreationMessageV2 || content.pollCreationMessageV3) {
      let p = this._polls.get(accountId);
      if (!p) this._polls.set(accountId, (p = new Map()));
      p.set(msg.key.id, msg);
      if (p.size > 200) p.delete(p.keys().next().value);
    }
  }

  _recall(accountId, id) {
    return this._recent.get(accountId)?.get(id);
  }

  _debug(entry) {
    this.debug.unshift({ at: new Date().toISOString(), ...entry });
    if (this.debug.length > 50) this.debug.pop();
  }

  /** Small TTL set so a reaction delivered on two Baileys channels forwards once. */
  _seenReaction(id) {
    if (!id) return false;
    this._reactionSeen ??= new Map();
    const now = Date.now();
    for (const [k, t] of this._reactionSeen) if (now - t > 60000) this._reactionSeen.delete(k);
    if (this._reactionSeen.has(id)) return true;
    this._reactionSeen.set(id, now);
    return false;
  }

  get(accountId) {
    return this.sessions.get(accountId);
  }

  snapshot(accountId) {
    const s = this.sessions.get(accountId);
    if (!s) return { status: "unknown", qr: null };
    return { status: s.status, qr: s.qr ?? null };
  }

  async start(accountId) {
    const existing = this.sessions.get(accountId);
    if (existing?.sock && ["connected", "waiting_for_scan", "connecting"].includes(existing.status)) {
      return this.snapshot(accountId);
    }

    const log = this.logger.child({ accountId });
    const { state, saveCreds } = await useMultiFileAuthState(
      path.join(config.sessionsDir, accountId),
    );
    const { version } = await fetchLatestBaileysVersion();

    const sock = makeWASocket({
      version,
      auth: state,
      printQRInTerminal: false,
      logger: this.logger.child({ accountId, comp: "baileys" }),
      markOnlineOnConnect: false,
    });

    this._setSession(accountId, { sock, qr: null, status: "connecting", reconnectAttempts: existing?.reconnectAttempts ?? 0 });

    sock.ev.on("creds.update", saveCreds);

    sock.ev.on("connection.update", async (update) => {
      const { connection, qr, lastDisconnect } = update;

      if (qr) {
        const qrImage = await QRCode.toDataURL(qr);
        this._patch(accountId, { qr: qrImage, status: "waiting_for_scan" });
        reportStatus(accountId, "waiting_for_scan", {}, log);
      }

      if (connection === "open") {
        const jid = sock.user?.id ?? null;
        this._patch(accountId, { qr: null, status: "connected", reconnectAttempts: 0 });
        log.info({ jid }, "connection open");
        reportStatus(accountId, "connected", { externalAccountId: jid }, log);
      }

      if (connection === "close") {
        const statusCode = new Boom(lastDisconnect?.error)?.output?.statusCode;
        const loggedOut = statusCode === DisconnectReason.loggedOut;
        this._patch(accountId, { sock: null, status: loggedOut ? "logged_out" : "disconnected" });
        reportStatus(accountId, loggedOut ? "logged_out" : "disconnected", {}, log);

        if (loggedOut) {
          log.warn("logged out - credentials no longer valid, awaiting re-connect");
          return;
        }
        this._scheduleReconnect(accountId, log);
      }
    });

    sock.ev.on("messages.upsert", async ({ messages, type }) => {
      for (const msg of messages) {
        const rawKeys = Object.keys(msg.message ?? {});
        if (!msg.message) {
          this._debug({ type, id: msg.key?.id, rawKeys, skipped: "no message body" });
          continue;
        }

        // Unwrap container nodes so `msg.message` holds the real content
        // (ephemeral / disappearing, view-once, captioned documents, ...).
        msg.message = unwrapMessage(msg.message);
        const contentKeys = Object.keys(msg.message ?? {});
        log.info({ id: msg.key?.id, type, rawKeys, contentKeys }, "message.upsert content");

        // Reactions arrive here as a reactionMessage node (more reliable than the
        // separate messages.reaction event, and carries the reactor's key).
        if (msg.message.reactionMessage) {
          const rm = msg.message.reactionMessage;
          const evt = {
            reactionEvent: {
              key: rm.key,                       // the message being reacted to
              reaction: { text: rm.text ?? "" }, // "" = removed
              reactorKey: msg.key,               // who reacted (participant / fromMe)
            },
          };
          this._debug({ type, id: msg.key?.id, target: rm.key?.id, emoji: rm.text || "(removed)", forwarded: "reaction" });
          if (this._seenReaction(msg.key?.id)) continue;
          await forwardMessage(accountId, evt, log);
          continue;
        }

        // Remember every message (any type) for quoted replies + poll votes.
        this._remember(accountId, msg);

        // "notify" = a genuinely new message; other types are history/appends.
        if (type !== "notify") {
          this._debug({ type, id: msg.key?.id, rawKeys, contentKeys, skipped: `type=${type}` });
          continue;
        }

        const media = await this._downloadMedia(accountId, msg, sock, log);
        log.info(
          { from: msg.key?.remoteJid, fromMe: !!msg.key?.fromMe, id: msg.key?.id, media: media?.type },
          "forwarding message to backend",
        );
        this._debug({ type, id: msg.key?.id, contentKeys, media: media?.type ?? null, forwarded: true });
        await forwardMessage(accountId, msg, log, media ? { media } : undefined);
      }
    });

    // Poll votes arrive as encrypted updates; decrypt against the stored poll message.
    sock.ev.on("messages.update", async (updates) => {
      for (const u of updates) {
        const pollUpdates = u.update?.pollUpdates;
        if (!pollUpdates?.length) continue;
        const pollMsg = this._recall(accountId, u.key?.id);
        if (!pollMsg) {
          log.warn({ id: u.key?.id }, "poll vote for an unknown poll (not in cache)");
          continue;
        }
        try {
          const meId = jidNormalizedUser(sock.user?.id);
          const agg = getAggregateVotesInPollMessage(
            { message: pollMsg.message, pollUpdates }, meId,
          );
          const voter = jidNormalizedUser(pollUpdates.at(-1)?.pollUpdateMessageKey?.participant
            ?? pollUpdates.at(-1)?.pollUpdateMessageKey?.remoteJid ?? u.key?.participant ?? u.key?.remoteJid);
          const selected = agg.filter(a => a.voters.includes(voter)).map(a => a.name);
          log.info({ poll: u.key?.id, voter, selected }, "forwarding poll vote");
          this._debug({ id: u.key?.id, voter, selected, forwarded: "poll vote" });
          await forwardMessage(accountId, {
            pollVote: { chatId: u.key?.remoteJid, pollMsgId: u.key?.id, voter, selectedOptions: selected },
          }, log);
        } catch (err) {
          log.warn({ err: err.message, id: u.key?.id }, "poll vote decrypt failed");
        }
      }
    });

    // Group membership changes.
    sock.ev.on("group-participants.update", async (ev) => {
      log.info({ jid: ev.id, action: ev.action, participants: ev.participants }, "forwarding group participants update");
      this._debug({ jid: ev.id, action: ev.action, forwarded: "group event" });
      await forwardMessage(accountId, {
        groupEvent: { jid: ev.id, type: ev.action, participants: ev.participants ?? [], author: ev.author ?? null },
      }, log);
    });

    // Group subject / description / settings changes, and being added to a group.
    const emitGroupUpdate = (g, joined) => forwardMessage(accountId, {
      groupEvent: {
        jid: g.id,
        type: joined ? "joined" : g.subject != null ? "subject" : g.desc != null ? "description" : "update",
        participants: (g.participants ?? []).map(p => p.id),
        subject: g.subject ?? null,
        description: typeof g.desc === "string" ? g.desc : (g.desc?.toString?.() ?? null),
      },
    }, log);
    sock.ev.on("groups.update", async (list) => { for (const g of list) await emitGroupUpdate(g, false); });
    sock.ev.on("groups.upsert", async (list) => { for (const g of list) await emitGroupUpdate(g, true); });

    // Secondary path: the dedicated reaction event. Deduped against the upsert
    // path above (some Baileys builds fire one, some the other, some both).
    sock.ev.on("messages.reaction", async (reactions) => {
      for (const r of reactions) {
        if (this._seenReaction(r.reaction?.key?.id ?? r.key?.id)) continue;
        log.info(
          { chat: r.key?.remoteJid, target: r.key?.id, emoji: r.reaction?.text || "(removed)" },
          "forwarding reaction to backend (messages.reaction)",
        );
        this._debug({ id: r.reaction?.key?.id ?? r.key?.id, target: r.key?.id, emoji: r.reaction?.text || "(removed)", forwarded: "reaction (event)" });
        await forwardMessage(accountId, { reactionEvent: { key: r.key, reaction: r.reaction, reactorKey: r.reaction?.key } }, log);
      }
    });

    return this.snapshot(accountId);
  }

  /** opts: { replyTo?: <messageId>, mentions?: [jid|number...] } */
  async send(accountId, to, text, opts = {}) {
    const { sock, jid } = this._ready(accountId, to);

    const content = { text };
    if (opts.mentions?.length) {
      content.mentions = opts.mentions.map(m =>
        m.includes("@") ? m : `${String(m).replace(/^\+/, "")}@s.whatsapp.net`);
    }

    const sendOpts = {};
    if (opts.replyTo) {
      const quoted = this._recall(accountId, opts.replyTo);
      if (quoted) sendOpts.quoted = quoted;
      else sendOpts.quoted = { key: { remoteJid: jid, id: opts.replyTo, fromMe: false }, message: {} };
    }

    const result = await sock.sendMessage(jid, content, sendOpts);
    this._remember(accountId, result);
    return { id: result?.key?.id ?? null, jid };
  }

  /** poll = { name, options: [string], selectableCount } */
  async sendPoll(accountId, to, poll) {
    const { sock, jid } = this._ready(accountId, to);
    const result = await sock.sendMessage(jid, {
      poll: {
        name: poll.name,
        values: poll.options,
        selectableCount: poll.selectableCount ?? 1,
      },
    });
    this._remember(accountId, result); // so incoming votes can be aggregated
    return { id: result?.key?.id ?? null, jid };
  }

  async groupMetadata(accountId, groupJid) {
    const { sock } = this._ready(accountId, groupJid);
    const g = await sock.groupMetadata(groupJid);
    return {
      id: g.id,
      subject: g.subject,
      description: g.desc ?? null,
      owner: g.owner ?? null,
      creation: g.creation ?? null,
      size: g.size ?? g.participants?.length ?? null,
      announce: !!g.announce,
      restrict: !!g.restrict,
      participants: (g.participants ?? []).map(p => ({
        id: p.id,
        admin: p.admin ?? null, // "admin" | "superadmin" | null
      })),
    };
  }

  /** React to a message. emoji "" removes the reaction. target = { id, fromMe?, participant? } */
  async react(accountId, to, target, emoji) {
    const { sock, jid } = this._ready(accountId, to);
    const key = {
      remoteJid: jid,
      id: target.id,
      fromMe: !!target.fromMe,
      ...(target.participant ? { participant: target.participant } : {}),
    };
    const result = await sock.sendMessage(jid, { react: { text: emoji ?? "", key } });
    return { id: result?.key?.id ?? null, jid };
  }

  /** media = { type, caption?, fileName?, mimetype?, ptt?, and one of: url | dataBase64 } */
  async sendMedia(accountId, to, media) {
    const { sock, jid } = this._ready(accountId, to);

    let buffer;
    if (media.dataBase64) {
      buffer = Buffer.from(media.dataBase64, "base64");
    } else if (media.url) {
      const res = await fetch(media.url);
      if (!res.ok) {
        const err = new Error(`could not fetch media url (${res.status})`);
        err.statusCode = 400;
        throw err;
      }
      buffer = Buffer.from(await res.arrayBuffer());
    } else {
      const err = new Error("media needs url or dataBase64");
      err.statusCode = 400;
      throw err;
    }

    if (buffer.length > config.maxMediaBytes) {
      const err = new Error(`media too large (${buffer.length} bytes)`);
      err.statusCode = 413;
      throw err;
    }

    const { type, caption, fileName, mimetype } = media;
    const content = {
      image: { image: buffer, caption },
      video: { video: buffer, caption },
      audio: { audio: buffer, mimetype: mimetype || "audio/mp4", ptt: !!media.ptt },
      document: {
        document: buffer,
        fileName: fileName || "file",
        mimetype: mimetype || "application/octet-stream",
        caption,
      },
    }[type];
    if (!content) {
      const err = new Error(`unknown media type '${type}'`);
      err.statusCode = 400;
      throw err;
    }

    const result = await sock.sendMessage(jid, content);
    this._remember(accountId, result);
    return { id: result?.key?.id ?? null, jid };
  }

  _ready(accountId, to) {
    const session = this.sessions.get(accountId);
    if (!session?.sock || session.status !== "connected") {
      const err = new Error("account not connected");
      err.statusCode = 409;
      throw err;
    }
    const jid = to.includes("@") ? to : `${to.replace(/^\+/, "")}@s.whatsapp.net`;
    return { sock: session.sock, jid };
  }

  /** Decrypt + persist any media on an inbound message; returns a served-URL descriptor. */
  async _downloadMedia(accountId, msg, sock, log) {
    const m = msg.message ?? {};
    const map = {
      imageMessage: "image", videoMessage: "video", audioMessage: "audio",
      documentMessage: "document", stickerMessage: "sticker",
    };
    const key = Object.keys(map).find((k) => m[k]);
    if (!key) return null;

    const node = m[key];
    try {
      log.info({ id: msg.key?.id, kind: key, mimetype: node.mimetype }, "downloading media");
      const buffer = await Promise.race([
        downloadMediaMessage(msg, "buffer", {}, { logger: log, reuploadRequest: sock.updateMediaMessage }),
        new Promise((_, rej) => setTimeout(() => rej(new Error("media download timed out")), 30000)),
      ]);
      const ext = (node.mimetype || "").split("/")[1]?.split(";")[0] || "bin";
      const dir = path.join(config.mediaDir, accountId);
      fs.mkdirSync(dir, { recursive: true });
      const file = `${msg.key.id}.${ext}`;
      fs.writeFileSync(path.join(dir, file), buffer);
      log.info({ id: msg.key?.id, file, bytes: buffer.length }, "media saved");

      return {
        type: map[key],
        url: `${config.publicUrl}/media/${accountId}/${file}`,
        mimetype: node.mimetype ?? null,
        fileName: node.fileName ?? file,
        size: buffer.length,
      };
    } catch (err) {
      log.warn({ err: err.message, id: msg.key?.id }, "media download failed");
      this._debug({ id: msg.key?.id, mediaDownloadError: err.message, kind: key });
      return null;
    }
  }

  async groups(accountId) {
    const session = this.sessions.get(accountId);
    if (!session?.sock || session.status !== "connected") {
      const err = new Error("account not connected");
      err.statusCode = 409;
      throw err;
    }
    const all = await session.sock.groupFetchAllParticipating();
    return Object.values(all).map((g) => ({
      id: g.id,
      subject: g.subject ?? null,
      size: g.size ?? g.participants?.length ?? null,
    }));
  }

  async logout(accountId) {
    const session = this.sessions.get(accountId);
    try {
      await session?.sock?.logout();
    } catch {
      // socket already gone - fall through and wipe local creds anyway
    } finally {
      try {
        session?.sock?.end?.(undefined);
      } catch {}
      this.sessions.delete(accountId);
      // Remove stored multi-file auth state so the next connect issues a fresh QR.
      fs.rmSync(path.join(config.sessionsDir, accountId), { recursive: true, force: true });
    }
  }

  _scheduleReconnect(accountId, log) {
    const session = this.sessions.get(accountId) ?? { reconnectAttempts: 0 };
    const attempt = (session.reconnectAttempts ?? 0) + 1;
    this._patch(accountId, { reconnectAttempts: attempt });

    const delay = Math.min(
      config.reconnectBaseDelay * 2 ** (attempt - 1),
      config.reconnectMaxDelay,
    );
    log.info({ attempt, delay }, "scheduling reconnect");
    setTimeout(() => {
      this.start(accountId).catch((err) => log.error({ err: err.message }, "reconnect failed"));
    }, delay);
  }

  _setSession(accountId, value) {
    this.sessions.set(accountId, value);
  }

  _patch(accountId, patch) {
    const current = this.sessions.get(accountId) ?? {};
    this.sessions.set(accountId, { ...current, ...patch });
  }
}

module.exports = { SessionManager };
