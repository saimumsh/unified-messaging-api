"use strict";

module.exports = {
  port: parseInt(process.env.PORT ?? "3001", 10),

  // .NET backend base URL and the shared secret both sides check.
  backendUrl: process.env.BACKEND_URL ?? "http://localhost:5080",
  connectorSecret: process.env.CONNECTOR_SECRET ?? "dev-connector-secret",

  // Where multi-file auth state for each account is stored.
  sessionsDir: process.env.SESSIONS_DIR ?? "./sessions",

  // Downloaded inbound media is written here and served from `${publicUrl}/media/...`.
  mediaDir: process.env.MEDIA_DIR ?? "./media",
  publicUrl: process.env.CONNECTOR_PUBLIC_URL ?? `http://localhost:${process.env.PORT ?? "3001"}`,
  maxMediaBytes: parseInt(process.env.MAX_MEDIA_BYTES ?? String(25 * 1024 * 1024), 10),

  // Auto-reconnect backoff (ms).
  reconnectBaseDelay: 2000,
  reconnectMaxDelay: 60000,
};
