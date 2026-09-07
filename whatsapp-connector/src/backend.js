"use strict";

const config = require("./config");

/**
 * Fire-and-forget-ish POST to the .NET backend with the shared secret header.
 * Logs and swallows errors so a backend blip never kills a WhatsApp socket.
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
  postToBackend("/api/internal/whatsapp/status", { accountId, status, ...extra }, logger);

const forwardMessage = (accountId, raw, logger, extra) =>
  postToBackend("/api/webhooks/whatsapp", { accountId, raw, ...extra }, logger);

module.exports = { postToBackend, reportStatus, forwardMessage };
