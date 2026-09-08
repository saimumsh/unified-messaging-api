"use strict";

const { fetch } = require("undici");
const config = require("./config");
const { parseSetCookie } = require("./cookies");

/**
 * Thin client over LinkedIn's internal Voyager API. Unofficial — see
 * LINKEDIN_PLAN.md §2. All traffic goes through the account's identity
 * (proxy + fingerprint) and carries the full cookie jar.
 */
class VoyagerClient {
  /**
   * @param {import("./cookies").CookieJar} jar
   * @param {import("./identity").Identity} identity
   * @param {import("pino").Logger} logger
   * @param {(map: Record<string,string>) => void} [onCookieRefresh]  called when LinkedIn rotates a cookie
   */
  constructor(jar, identity, logger, onCookieRefresh) {
    this.jar = jar;
    this.identity = identity;
    this.logger = logger;
    this._onCookieRefresh = onCookieRefresh;
  }

  get jsessionid() {
    return this.jar.csrfToken;
  }

  get cookieHeader() {
    return this.jar.header();
  }

  headers(extra = {}) {
    return {
      ...this.identity.baseHeaders(),
      cookie: this.cookieHeader,
      "csrf-token": this.jar.csrfToken,
      accept: "application/vnd.linkedin.normalized+json+2.1",
      ...extra,
    };
  }

  /** Fold any Set-Cookie from a response back into the jar (keeps sessions alive longer). */
  _absorb(res) {
    const sc = typeof res.headers.getSetCookie === "function" ? res.headers.getSetCookie() : null;
    const raw = sc && sc.length ? sc : res.headers.get("set-cookie");
    if (!raw) return;
    const fresh = parseSetCookie(raw);
    if (Object.keys(fresh).length) {
      this.jar.merge(fresh);
      this._onCookieRefresh?.(fresh);
    }
  }

  async _get(pathOrUrl) {
    const url = pathOrUrl.startsWith("http") ? pathOrUrl : `${config.voyagerBase}${pathOrUrl}`;
    const res = await fetch(url, {
      method: "GET",
      headers: this.headers(),
      dispatcher: this.identity.dispatcher,
      redirect: "manual",
    });
    return this._handle(res, url);
  }

  async _post(path, body, extraHeaders = {}) {
    const url = `${config.voyagerBase}${path}`;
    const res = await fetch(url, {
      method: "POST",
      headers: this.headers({ "content-type": "application/json; charset=UTF-8", ...extraHeaders }),
      dispatcher: this.identity.dispatcher,
      redirect: "manual",
      body: typeof body === "string" ? body : JSON.stringify(body),
    });
    return this._handle(res, url);
  }

  async _handle(res, url) {
    this._absorb(res);

    // A redirect to /checkpoint / /uas/login means the session is challenged or dead.
    const location = res.headers.get("location") || "";
    if ([301, 302, 303, 307, 308].includes(res.status) || /checkpoint|uas\/login/i.test(location)) {
      throw taggedError("challenge", `LinkedIn redirected to ${location || "(login/checkpoint)"}`);
    }
    if (res.status === 401 || res.status === 403) {
      const text = await res.text().catch(() => "");
      if (/CSRF|challenge|checkpoint/i.test(text) || res.status === 401) {
        throw taggedError("challenge", `Voyager ${res.status} on ${short(url)}`);
      }
      throw taggedError("forbidden", `Voyager ${res.status} on ${short(url)}`);
    }
    if (res.status === 429 || res.status === 999) {
      throw taggedError("ratelimit", `Voyager ${res.status} on ${short(url)}`);
    }
    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw taggedError("http", `Voyager ${res.status} on ${short(url)}: ${text.slice(0, 200)}`);
    }
    const ct = res.headers.get("content-type") || "";
    return ct.includes("json") ? res.json() : res.text();
  }

  // ---- API surface ----

  /** Own member URN, e.g. "urn:li:fsd_profile:ACoAAB...". */
  async me() {
    const data = await this._get("/me");
    const mini = data?.included?.find((x) => x?.$type?.includes("MiniProfile")) ?? data;
    const urn =
      data?.data?.miniProfile ||
      data?.data?.["*miniProfile"] ||
      mini?.entityUrn ||
      mini?.objectUrn ||
      null;
    return normalizeMemberUrn(urn);
  }

  /** Recent conversations (newest first). */
  async listConversations({ limit = 20 } = {}) {
    const data = await this._get(
      `/messaging/conversations?keyVersion=LEGACY_INBOX&q=syncToken&count=${limit}`,
    );
    return data;
  }

  /** Events (messages) in one conversation thread. */
  async conversationEvents(threadId, { limit = 20 } = {}) {
    return this._get(
      `/messaging/conversations/${encodeURIComponent(threadId)}/events?count=${limit}`,
    );
  }

  /**
   * Send a text message into an existing thread.
   * @param {string} threadId  e.g. "2-abc=="
   */
  async sendToThread(threadId, text) {
    const body = {
      eventCreate: {
        originToken: require("crypto").randomUUID(),
        value: {
          "com.linkedin.voyager.messaging.create.MessageCreate": {
            attachments: [],
            body: text,
            attributedBody: { text, attributes: [] },
            mediaAttachments: [],
          },
        },
      },
      dedupeByClientGeneratedToken: false,
    };
    const res = await this._post(
      `/messaging/conversations/${encodeURIComponent(threadId)}/events?action=create`,
      body,
    );
    const eventUrn =
      res?.data?.value?.eventUrn ||
      res?.value?.eventUrn ||
      res?.data?.eventUrn ||
      null;
    return { id: eventUrn, threadId };
  }

  /**
   * Start a new 1:1 conversation with a member and send the first message.
   * @param {string} memberUrn  "urn:li:fsd_profile:..." or bare id
   */
  async sendToMember(memberUrn, text) {
    const urn = normalizeMemberUrn(memberUrn);
    const body = {
      keyVersion: "LEGACY_INBOX",
      conversationCreate: {
        eventCreate: {
          value: {
            "com.linkedin.voyager.messaging.create.MessageCreate": {
              attachments: [],
              body: text,
              attributedBody: { text, attributes: [] },
              mediaAttachments: [],
            },
          },
        },
        recipients: [urn],
        subtype: "MEMBER_TO_MEMBER",
      },
    };
    const res = await this._post(`/messaging/conversations?action=create`, body);
    const threadId =
      res?.data?.value?.conversationUrn?.split(":").pop() ||
      res?.data?.conversationUrn?.split(":").pop() ||
      null;
    const eventUrn = res?.data?.value?.eventUrn || res?.data?.eventUrn || null;
    return { id: eventUrn, threadId };
  }

  async markRead(threadId) {
    return this._post(`/messaging/conversations/${encodeURIComponent(threadId)}?action=markAllAsRead`, {
      patch: { $set: { read: true } },
    });
  }
}

function short(url) {
  return String(url).replace(config.voyagerBase, "");
}

function taggedError(kind, message) {
  const err = new Error(message);
  err.kind = kind; // "challenge" | "ratelimit" | "forbidden" | "http"
  return err;
}

/** "urn:li:fsd_profile:X" / "urn:li:member:123" / bare id -> "urn:li:fsd_profile:X". */
function normalizeMemberUrn(urn) {
  if (!urn) return null;
  if (urn.startsWith("urn:li:fsd_profile:")) return urn;
  if (urn.startsWith("urn:li:")) {
    const id = urn.split(":").pop();
    return `urn:li:fsd_profile:${id}`;
  }
  return `urn:li:fsd_profile:${urn}`;
}

module.exports = { VoyagerClient, normalizeMemberUrn };
