"use strict";

const { EventEmitter } = require("events");
const { request } = require("undici");
const config = require("./config");

/**
 * Best-effort long-poll / SSE reader for LinkedIn's realtime stream. The
 * conversation poller in sessionManager is the reliable path; this just lowers
 * latency when it works. Emits:
 *   "event"  -> raw DecoratedEvent payload object
 *   "error"  -> Error (caller schedules a reconnect)
 *   "challenge" -> Error (caller flips the account to needs_reauth)
 */
class RealtimeStream extends EventEmitter {
  constructor(voyager, logger) {
    super();
    this.voyager = voyager;
    this.logger = logger;
    this._abort = null;
    this._stopped = false;
  }

  async start() {
    this._stopped = false;
    const url = `${config.linkedInBase}/realtime/connect?rc=1`;
    const ac = new AbortController();
    this._abort = ac;

    let res;
    try {
      res = await request(url, {
        method: "GET",
        headers: {
          ...this.voyager.identity.baseHeaders(),
          cookie: this.voyager.cookieHeader,
          "csrf-token": this.voyager.jsessionid,
          accept: "text/event-stream",
        },
        dispatcher: this.voyager.identity.dispatcher,
        signal: ac.signal,
        headersTimeout: 0,
        bodyTimeout: 0,
      });
    } catch (err) {
      if (!this._stopped) this.emit("error", err);
      return;
    }

    if (res.statusCode === 401 || res.statusCode === 403) {
      this.emit("challenge", new Error(`realtime ${res.statusCode}`));
      return;
    }
    if (res.statusCode === 429 || res.statusCode === 999) {
      const e = new Error(`realtime ${res.statusCode}`);
      e.kind = "ratelimit";
      this.emit("error", e);
      return;
    }
    if (res.statusCode >= 300) {
      this.emit("error", new Error(`realtime HTTP ${res.statusCode}`));
      return;
    }

    this.logger.info("realtime stream open");
    let buf = "";
    try {
      for await (const chunk of res.body) {
        buf += chunk.toString("utf8");
        let idx;
        while ((idx = buf.indexOf("\n\n")) !== -1) {
          const block = buf.slice(0, idx);
          buf = buf.slice(idx + 2);
          this._handleBlock(block);
        }
      }
      if (!this._stopped) this.emit("error", new Error("realtime stream ended"));
    } catch (err) {
      if (!this._stopped) this.emit("error", err);
    }
  }

  _handleBlock(block) {
    const dataLine = block
      .split("\n")
      .filter((l) => l.startsWith("data:"))
      .map((l) => l.slice(5).trim())
      .join("");
    if (!dataLine) return;
    let json;
    try {
      json = JSON.parse(dataLine);
    } catch {
      return;
    }
    const decorated =
      json["com.linkedin.realtimefrontend.DecoratedEvent"] ||
      json?.payload?.["com.linkedin.realtimefrontend.DecoratedEvent"] ||
      json;
    this.emit("event", decorated);
  }

  stop() {
    this._stopped = true;
    try {
      this._abort?.abort();
    } catch {
      /* ignore */
    }
  }
}

module.exports = { RealtimeStream };
