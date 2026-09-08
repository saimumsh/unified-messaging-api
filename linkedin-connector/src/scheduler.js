"use strict";

const fs = require("fs");
const path = require("path");
const config = require("./config");

/**
 * Per-account action scheduler (§ "If you're building this", item 4) — the piece
 * that keeps customers from banning themselves by volume.
 *
 *  - rolling-24h daily caps per action kind (send / invite / read / search),
 *    persisted to sessions/<id>/usage.json so a restart doesn't reset them
 *  - short-window burst guards (per-hour sends, per-minute reads)
 *  - a minimum gap between actions + random jitter (human rhythm)
 *  - quiet hours (no outbound actions overnight in the account's timezone)
 *  - a hard cooldown after an observed 429 / LinkedIn 999
 *
 * `await schedule(kind)` resolves when it is safe to act, or throws a 429-tagged
 * error if a daily cap is already exhausted.
 */
class Scheduler {
  /** @param {string} accountId  @param {string|null} timezone  IANA tz from identity */
  constructor(accountId, timezone) {
    this.accountId = accountId;
    this.timezone = timezone || "UTC";
    this.cooldownUntil = 0;
    this.lastActionAt = 0;
    this.hour = ring(3600_000);
    this.minute = ring(60_000);
    this.usage = loadUsage(accountId); // { send: [ts,...], invite: [...], ... }
  }

  cooldown(ms = config.rateLimitCooldownMs) {
    this.cooldownUntil = Math.max(this.cooldownUntil, Date.now() + ms);
  }
  get inCooldown() {
    return Date.now() < this.cooldownUntil;
  }

  /** How many of `kind` are still allowed in the rolling 24h window. */
  remaining(kind) {
    const cap = config.dailyCaps[kind] ?? Infinity;
    return Math.max(0, cap - this._count(kind));
  }

  /**
   * @param {"send"|"invite"|"read"|"search"} kind
   * @param {{ outbound?: boolean }} opts  outbound actions also respect quiet hours
   */
  async schedule(kind, { outbound = kind !== "read" } = {}) {
    if (this.inCooldown) {
      throw rl(`rate-limit cooldown ${Math.ceil((this.cooldownUntil - Date.now()) / 1000)}s`);
    }

    // Daily cap — hard stop.
    if (this._count(kind) >= (config.dailyCaps[kind] ?? Infinity)) {
      throw rl(`daily ${kind} cap reached (${config.dailyCaps[kind]}/24h)`);
    }

    // Short-window burst guards.
    if (kind === "send" && this.hour.count() >= config.rlSendsPerHour) {
      throw rl(`hourly send cap reached (${config.rlSendsPerHour}/h)`);
    }
    if (kind === "read" && this.minute.count() >= config.rlReadsPerMin) {
      await sleep(this.minute.msUntilRoom(config.rlReadsPerMin));
    }

    // Quiet hours (outbound only).
    if (outbound) {
      const wait = this._quietHoursWaitMs();
      if (wait > 0) throw rl(`quiet hours — next window in ${Math.ceil(wait / 60000)}m`);
    }

    // Human gap + jitter.
    const since = Date.now() - this.lastActionAt;
    const need = config.minActionGapMs + Math.floor(Math.random() * config.actionJitterMs);
    if (since < need) await sleep(need - since);

    // Commit.
    const now = Date.now();
    this.lastActionAt = now;
    this.hour.push(now);
    this.minute.push(now);
    (this.usage[kind] ??= []).push(now);
    this._prune();
    saveUsage(this.accountId, this.usage);
  }

  snapshot() {
    return {
      inCooldown: this.inCooldown,
      remaining: Object.fromEntries(
        Object.keys(config.dailyCaps).map((k) => [k, this.remaining(k)]),
      ),
    };
  }

  _count(kind) {
    const cutoff = Date.now() - 86_400_000;
    return (this.usage[kind] || []).filter((t) => t > cutoff).length;
  }

  _prune() {
    const cutoff = Date.now() - 86_400_000;
    for (const k of Object.keys(this.usage)) {
      this.usage[k] = (this.usage[k] || []).filter((t) => t > cutoff);
    }
  }

  /** ms to wait until we exit quiet hours, or 0 if we're outside them. */
  _quietHoursWaitMs() {
    if (!config.quietHours) return 0;
    const m = /^(\d{1,2})\s*-\s*(\d{1,2})$/.exec(config.quietHours);
    if (!m) return 0;
    const start = +m[1];
    const end = +m[2];
    const hour = localHour(this.timezone);
    const inQuiet = start < end ? hour >= start && hour < end : hour >= start || hour < end;
    if (!inQuiet) return 0;
    // Rough: minutes until `end` o'clock.
    let hrs = (end - hour + 24) % 24;
    if (hrs === 0) hrs = 24;
    return hrs * 3600_000;
  }
}

// ---- rolling-window counter ----
function ring(windowMs) {
  const arr = [];
  return {
    push(ts) {
      arr.push(ts);
    },
    count() {
      const c = Date.now() - windowMs;
      while (arr.length && arr[0] < c) arr.shift();
      return arr.length;
    },
    msUntilRoom(cap) {
      this.count();
      if (arr.length < cap) return 0;
      return Math.max(0, arr[arr.length - cap] + windowMs - Date.now());
    },
  };
}

function localHour(tz) {
  try {
    return parseInt(
      new Intl.DateTimeFormat("en-US", { hour: "2-digit", hour12: false, timeZone: tz }).format(
        new Date(),
      ),
      10,
    ) % 24;
  } catch {
    return new Date().getUTCHours();
  }
}

function usageFile(accountId) {
  return path.join(config.sessionsDir, accountId, "usage.json");
}
function loadUsage(accountId) {
  try {
    return JSON.parse(fs.readFileSync(usageFile(accountId), "utf8"));
  } catch {
    return {};
  }
}
function saveUsage(accountId, usage) {
  try {
    const dir = path.join(config.sessionsDir, accountId);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(usageFile(accountId), JSON.stringify(usage));
  } catch {
    /* best effort */
  }
}

const sleep = (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms)));
function rl(msg) {
  const e = new Error(msg);
  e.statusCode = 429;
  e.kind = "ratelimit";
  return e;
}

module.exports = { Scheduler };
