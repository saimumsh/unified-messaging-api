"use strict";

const fs = require("fs");
const path = require("path");
const { ProxyAgent, Agent } = require("undici");

const config = require("./config");

/**
 * Per-account network identity — the core ban-avoidance mechanism (§6.1 / §6.2).
 * Built once at connect time and persisted in sessions/<id>/identity.json so every
 * Voyager request and the realtime stream for that account always egress from the
 * same IP with the same fingerprint.
 */
class Identity {
  /**
   * @param {string} accountId
   * @param {{ proxyUrl?: string, userAgent?: string, acceptLang?: string, deviceId?: string, timezone?: string }} data
   */
  constructor(accountId, data) {
    this.accountId = accountId;
    this.proxyUrl = data.proxyUrl || null;
    this.userAgent = data.userAgent || config.defaultUserAgent;
    this.acceptLang = data.acceptLang || "en-US,en;q=0.9";
    this.deviceId = data.deviceId || cryptoRandomId();
    this.timezone = data.timezone || "UTC"; // IANA tz — drives the scheduler's quiet hours
    this._dispatcher = this.proxyUrl
      ? new ProxyAgent(this.proxyUrl)
      : new Agent();
  }

  /** Swap the proxy without losing the fingerprint (Phase 3 rotation). */
  setProxyUrl(url) {
    if (url === this.proxyUrl) return;
    const old = this._dispatcher;
    this.proxyUrl = url || null;
    this._dispatcher = this.proxyUrl ? new ProxyAgent(this.proxyUrl) : new Agent();
    Promise.resolve(old?.close?.()).catch(() => {});
  }

  /** undici dispatcher — pass as `{ dispatcher }` to fetch / request. */
  get dispatcher() {
    return this._dispatcher;
  }

  /** Stable browser-ish header set for this account. */
  baseHeaders() {
    return {
      "user-agent": this.userAgent,
      "accept-language": this.acceptLang,
      "x-li-lang": "en_US",
      "x-restli-protocol-version": "2.0.0",
      "x-li-track": JSON.stringify({
        clientVersion: "1.13.0",
        mpVersion: "1.13.0",
        osName: "web",
        timezoneOffset: 0,
        timezone: "UTC",
        deviceFormFactor: "DESKTOP",
        mpName: "voyager-web",
        displayDensity: 2,
        displayWidth: 2560,
        displayHeight: 1440,
      }),
      "x-li-page-instance": `urn:li:page:messaging_index;${this.deviceId}`,
    };
  }

  toJSON() {
    return {
      proxyUrl: this.proxyUrl,
      userAgent: this.userAgent,
      acceptLang: this.acceptLang,
      deviceId: this.deviceId,
      timezone: this.timezone,
    };
  }

  async close() {
    try {
      await this._dispatcher.close();
    } catch {
      /* ignore */
    }
  }

  static file(accountId) {
    return path.join(config.sessionsDir, accountId, "identity.json");
  }

  static load(accountId) {
    try {
      const raw = fs.readFileSync(Identity.file(accountId), "utf8");
      return new Identity(accountId, JSON.parse(raw));
    } catch {
      return null;
    }
  }

  save() {
    const dir = path.join(config.sessionsDir, this.accountId);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(Identity.file(this.accountId), JSON.stringify(this.toJSON(), null, 2));
  }
}

function cryptoRandomId() {
  return require("crypto").randomUUID();
}

module.exports = { Identity };
