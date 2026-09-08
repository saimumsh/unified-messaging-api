"use strict";

const config = require("./config");
const { CookieJar } = require("./cookies");

/**
 * Browser-assisted login (§ "If you're building this", item 2). LinkedIn's
 * username/password flow is JS-heavy and fingerprinted, so we drive a real
 * headless Chromium *once* — through the account's proxy — solve any checkpoint,
 * extract the full cookie jar, then hand back to the pure-HTTP Voyager client.
 *
 * Playwright is an optional dependency. If it isn't installed this module throws
 * a clear error and the connector falls back to cookie-paste auth.
 */

let _playwright = null;
function playwright() {
  if (_playwright) return _playwright;
  try {
    _playwright = require("playwright");
  } catch {
    const e = new Error(
      "username/password login needs Playwright — run `npm i playwright && npx playwright install chromium` " +
        "in linkedin-connector/, or connect with a cookie instead",
    );
    e.statusCode = 501;
    throw e;
  }
  return _playwright;
}

/**
 * A login attempt that may pause on a checkpoint. Kept in memory by the caller
 * (sessionManager) keyed by accountId so a follow-up /challenge call can resume.
 */
class LoginAttempt {
  constructor(accountId, identity, logger) {
    this.accountId = accountId;
    this.identity = identity;
    this.logger = logger;
    this.browser = null;
    this.context = null;
    this.page = null;
    this.state = "idle"; // idle | checkpoint | done | failed
    this._closeTimer = null;
  }

  async _open() {
    const { chromium } = playwright();
    const launch = { headless: config.loginHeadless };
    if (this.identity.proxyUrl) {
      const u = new URL(this.identity.proxyUrl);
      launch.proxy = {
        server: `${u.protocol}//${u.host}`,
        username: decodeURIComponent(u.username || ""),
        password: decodeURIComponent(u.password || ""),
      };
    }
    this.browser = await chromium.launch(launch);
    this.context = await this.browser.newContext({
      userAgent: this.identity.userAgent,
      locale: (this.identity.acceptLang || "en-US").split(",")[0],
      viewport: { width: 1280, height: 800 },
    });
    this.page = await this.context.newPage();
    // Auto-clean if the caller never finishes the challenge.
    this._closeTimer = setTimeout(() => this.close(), 10 * 60 * 1000).unref?.();
  }

  /** @returns {Promise<{ status: "checkpoint" } | { status: "connected", jar: CookieJar }>} */
  async start(username, password) {
    if (!config.loginEnabled) {
      const e = new Error("password login disabled (LOGIN_ENABLED=false)");
      e.statusCode = 403;
      throw e;
    }
    await this._open();
    await this.page.goto("https://www.linkedin.com/login", { waitUntil: "domcontentloaded" });
    await this.page.fill("#username", username);
    await this.page.fill("#password", password);
    await this.page.click('button[type="submit"]');
    await this.page.waitForLoadState("networkidle").catch(() => {});
    return this._resolve();
  }

  /** Submit the emailed / SMS / authenticator code for a pending checkpoint. */
  async submitChallenge(code) {
    if (this.state !== "checkpoint") {
      const e = new Error("no checkpoint is pending for this account");
      e.statusCode = 409;
      throw e;
    }
    const input = await this.page
      .locator('input[name="pin"], input[autocomplete="one-time-code"], #input__phone_verification_pin')
      .first();
    await input.fill(code);
    await this.page
      .locator('button[type="submit"], #two-step-submit-button')
      .first()
      .click();
    await this.page.waitForLoadState("networkidle").catch(() => {});
    return this._resolve();
  }

  async _resolve() {
    const url = this.page.url();
    if (/\/checkpoint\/|\/challenge\//.test(url)) {
      this.state = "checkpoint";
      this.logger.warn({ url }, "linkedin login checkpoint — awaiting code");
      return { status: "checkpoint" };
    }
    if (/\/feed\/?|\/mynetwork\/|linkedin\.com\/?$/.test(url) || url.includes("/feed")) {
      const jar = await this._extractJar();
      this.state = "done";
      await this.close();
      return { status: "connected", jar };
    }
    // Unknown page — treat as failure but surface what we saw.
    this.state = "failed";
    const title = await this.page.title().catch(() => "");
    await this.close();
    const e = new Error(`login did not reach the feed (at ${url} — "${title}")`);
    e.statusCode = 401;
    throw e;
  }

  async _extractJar() {
    const cookies = await this.context.cookies("https://www.linkedin.com");
    const map = {};
    for (const c of cookies) map[c.name] = c.value;
    return new CookieJar(map);
  }

  async close() {
    clearTimeout(this._closeTimer);
    try {
      await this.browser?.close();
    } catch {
      /* ignore */
    }
    this.browser = this.context = this.page = null;
  }
}

module.exports = { LoginAttempt };
