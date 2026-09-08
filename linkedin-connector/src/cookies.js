"use strict";

/**
 * Full LinkedIn cookie jar (§ "What Unipile does" — capturing the whole jar, not
 * just li_at, is what keeps a session alive for weeks and passes more checks).
 *
 * Accepts, in order of preference:
 *   1. { cookies: [ {name,value}, ... ] }            (browser export / Playwright)
 *   2. { cookieHeader: "li_at=..; JSESSIONID=..; .." }  (DevTools "copy as cURL")
 *   3. { li_at, jsessionid, li_a?, ... }              (individual fields)
 */

// Cookies we care about keeping (others are passed through if present).
const KNOWN = [
  "li_at", // primary auth
  "JSESSIONID", // csrf token + session
  "li_a", // Sales Navigator / Recruiter seat
  "liap", // "logged in at premium"
  "li_gc", // guest consent
  "li_mc", // member consent
  "bcookie", // browser id (stable fingerprint)
  "bscookie", // secure browser id
  "lang",
  "lidc", // datacenter routing
  "li_rm", // remember-me
  "JSESSIONID",
  "timezone",
];

class CookieJar {
  /** @param {Record<string,string>} map */
  constructor(map) {
    this.map = {};
    for (const [k, v] of Object.entries(map)) {
      if (v == null || v === "") continue;
      this.map[k] = String(v);
    }
    if (!this.map.li_at) throw badRequest("cookie jar is missing li_at");
    if (!this.map.JSESSIONID) throw badRequest("cookie jar is missing JSESSIONID");
  }

  get liAt() {
    return this.map.li_at;
  }

  /** csrf token = JSESSIONID value without surrounding quotes. */
  get csrfToken() {
    return String(this.map.JSESSIONID).replace(/^"|"$/g, "");
  }

  /** `Cookie:` request header. JSESSIONID must stay quoted. */
  header() {
    return Object.entries(this.map)
      .map(([k, v]) => {
        if (k === "JSESSIONID") {
          const raw = String(v).replace(/^"|"$/g, "");
          return `JSESSIONID="${raw}"`;
        }
        return `${k}=${v}`;
      })
      .join("; ");
  }

  toJSON() {
    return { ...this.map };
  }

  /** Merge fresh cookies (e.g. a rotated JSESSIONID from a response) in place. */
  merge(map) {
    for (const [k, v] of Object.entries(map || {})) {
      if (v != null && v !== "") this.map[k] = String(v);
    }
  }
}

/** Build a CookieJar from any of the accepted input shapes. */
function parseJar(input = {}) {
  // 1. explicit cookie array
  if (Array.isArray(input.cookies)) {
    const map = {};
    for (const c of input.cookies) {
      if (c && c.name) map[c.name] = c.value;
    }
    return new CookieJar(map);
  }

  // 2. raw Cookie header string
  const headerStr = input.cookieHeader || input.cookie;
  if (typeof headerStr === "string" && headerStr.includes("=")) {
    return new CookieJar(parseCookieHeader(headerStr));
  }

  // 3. individual fields (li_at + jsessionid + any extras)
  const map = {};
  if (input.li_at) map.li_at = input.li_at;
  if (input.jsessionid || input.JSESSIONID) map.JSESSIONID = input.jsessionid || input.JSESSIONID;
  for (const name of KNOWN) {
    const lower = name.toLowerCase();
    if (input[name] != null) map[name] = input[name];
    else if (input[lower] != null && !map[name]) map[name] = input[lower];
  }
  return new CookieJar(map);
}

function parseCookieHeader(str) {
  const map = {};
  const cleaned = String(str).replace(/^\s*cookie:\s*/i, "");
  for (const part of cleaned.split(/;\s*/)) {
    const i = part.indexOf("=");
    if (i === -1) continue;
    const name = part.slice(0, i).trim();
    const value = part.slice(i + 1).trim();
    if (name) map[name] = value;
  }
  return map;
}

/** Parse a Set-Cookie response header list into { name: value }. */
function parseSetCookie(setCookieHeaders) {
  const map = {};
  for (const line of [].concat(setCookieHeaders || [])) {
    const first = String(line).split(";", 1)[0];
    const i = first.indexOf("=");
    if (i === -1) continue;
    map[first.slice(0, i).trim()] = first.slice(i + 1).trim();
  }
  return map;
}

function badRequest(msg) {
  const e = new Error(msg);
  e.statusCode = 400;
  return e;
}

module.exports = { CookieJar, parseJar, parseCookieHeader, parseSetCookie };
