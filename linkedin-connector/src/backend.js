"use strict";

const config = require("./config");

/**
 * Fire-and-forget-ish POST to the .NET backend with the shared secret header.
 * Logs and swallows errors so a backend blip never kills a session.
 */
async function postToBackend(path, body, logger) {
  const url = `${config.backendUrl}${path}`;
  try {
    const res = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Connector-Secret": config.connectorSecret,
      },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      const text = await res.text().catch(() => "");
      logger?.warn({ url, status: res.status, text }, "backend rejected event");
    }
  } catch (err) {
    logger?.error({ url, err: err.message }, "failed to reach backend");
  }
}

const reportStatus = (accountId, status, extra, logger) =>
  postToBackend("/api/internal/linkedin/status", { accountId, status, ...extra }, logger);

// `raw` is the normalized LinkedIn message shape the .NET LinkedInAdapter expects:
// { threadId, eventUrn, from, fromMe, subject?, body:{text}, attachments[], createdAt }
const forwardMessage = (accountId, raw, logger) =>
  postToBackend("/api/webhooks/linkedin", { accountId, raw }, logger);

module.exports = { postToBackend, reportStatus, forwardMessage };
