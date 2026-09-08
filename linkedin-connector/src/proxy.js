"use strict";

const { fetch } = require("undici");
const config = require("./config");

/**
 * Managed proxy layer (§ "If you're building this", item 1). The operator never
 * pastes a `user:pass@host:port` string — on connect we resolve a per-account
 * sticky residential endpoint from whichever provider is configured, and persist
 * it in identity.json so the account always egresses from the same IP.
 *
 * PROXY_PROVIDER:
 *   manual   -> use the proxyUrl supplied on /connect (default)
 *   webshare -> pick a sticky endpoint from Webshare's API (WEBSHARE_API_KEY)
 *   none     -> no proxy (dev only)
 */

/**
 * @param {{ accountId: string, suppliedProxyUrl?: string, storedProxyUrl?: string, country?: string }} opts
 * @returns {Promise<string|null>} a proxy URL, or null when none is used
 */
async function resolveProxy(opts, logger) {
  const { accountId, suppliedProxyUrl, storedProxyUrl, country } = opts;

  // An explicit URL on this call always wins (lets you override / rotate).
  if (suppliedProxyUrl) return toProxyUrl(suppliedProxyUrl);

  switch (config.proxyProvider) {
    case "none":
      return null;

    case "manual":
      if (storedProxyUrl) return toProxyUrl(storedProxyUrl);
      if (config.proxyRotatingEndpoint) return toProxyUrl(config.proxyRotatingEndpoint);
      if (config.requireProxy) {
        throw badRequest(
          "proxyUrl is required (PROXY_PROVIDER=manual). Supply one on /connect, " +
            "set PROXY_ROTATING_ENDPOINT, or switch PROXY_PROVIDER to webshare/none.",
        );
      }
      return null;

    case "webshare": {
      // Reuse the stored one if we already provisioned for this account.
      if (storedProxyUrl) return toProxyUrl(storedProxyUrl);
      const url = await webshareSticky(accountId, country ?? config.proxyDefaultCountry, logger);
      if (!url && config.requireProxy) {
        throw badGateway("webshare returned no usable proxy for this account");
      }
      return url;
    }

    default:
      throw badRequest(`unknown PROXY_PROVIDER '${config.proxyProvider}'`);
  }
}

// ---- Webshare driver ------------------------------------------------------

/**
 * Webshare hands out a list of proxies on the account; we deterministically map
 * an accountId to one of them so each LinkedIn account keeps a stable IP.
 * Docs: https://apidocs.webshare.io/  (proxy/list endpoint)
 */
async function webshareSticky(accountId, country, logger) {
  if (!config.webshareApiKey) {
    throw badRequest("PROXY_PROVIDER=webshare needs WEBSHARE_API_KEY");
  }
  const params = new URLSearchParams({ mode: "direct", page: "1", page_size: "100" });
  if (country) params.set("country_code__in", country);

  const res = await fetch(`https://proxy.webshare.io/api/v2/proxy/list/?${params}`, {
    headers: { Authorization: `Token ${config.webshareApiKey}` },
  });
  if (!res.ok) {
    const body = await res.text().catch(() => "");
    throw badGateway(`webshare proxy/list ${res.status}: ${body.slice(0, 200)}`);
  }
  const data = await res.json();
  const list = (data.results || []).filter((p) => p.valid !== false);
  if (!list.length) return null;

  // Stable pick: hash the accountId to an index.
  const idx = hashToIndex(accountId, list.length);
  const p = list[idx];
  const auth = `${encodeURIComponent(p.username)}:${encodeURIComponent(p.password)}`;
  const url = `http://${auth}@${p.proxy_address}:${p.port}`;
  logger?.info(
    { accountId, proxy: `${p.proxy_address}:${p.port}`, country: p.country_code },
    "assigned webshare proxy",
  );
  return url;
}

// ---- helpers ------------------------------------------------------------

/**
 * Accept any of:
 *   http://user:pass@host:port          (full URL — passed through)
 *   host:port:user:pass                 (common provider paste format)
 *   host:port                           (no auth)
 */
function toProxyUrl(s) {
  if (!s) return null;
  if (/^\w+:\/\//.test(s)) return s;
  const parts = s.split(":");
  if (parts.length === 4) {
    const [host, port, user, pass] = parts;
    return `http://${encodeURIComponent(user)}:${encodeURIComponent(pass)}@${host}:${port}`;
  }
  return `http://${s}`;
}

function hashToIndex(str, mod) {
  let h = 0;
  for (let i = 0; i < str.length; i++) h = (h * 31 + str.charCodeAt(i)) >>> 0;
  return h % mod;
}

function badRequest(msg) {
  const e = new Error(msg);
  e.statusCode = 400;
  return e;
}
function badGateway(msg) {
  const e = new Error(msg);
  e.statusCode = 502;
  return e;
}

module.exports = { resolveProxy, hashToIndex, toProxyUrl };
