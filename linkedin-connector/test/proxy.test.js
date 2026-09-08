"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { hashToIndex, toProxyUrl } = require("../src/proxy");

test("hashToIndex is stable and in range", () => {
  const a = hashToIndex("account-abc", 10);
  const b = hashToIndex("account-abc", 10);
  assert.equal(a, b);
  assert.ok(a >= 0 && a < 10);
  // different account -> (very likely) different slot
  assert.notEqual(hashToIndex("account-xyz", 100), hashToIndex("account-abc", 100));
});

test("toProxyUrl expands host:port:user:pass", () => {
  assert.equal(
    toProxyUrl("gate.example.com:7000:bob:s3cr3t"),
    "http://bob:s3cr3t@gate.example.com:7000",
  );
});

test("toProxyUrl passes a full URL through and prefixes a bare host:port", () => {
  assert.equal(toProxyUrl("http://u:p@h:1"), "http://u:p@h:1");
  assert.equal(toProxyUrl("socks5://u:p@h:1"), "socks5://u:p@h:1");
  assert.equal(toProxyUrl("1.2.3.4:8080"), "http://1.2.3.4:8080");
});

test("resolveProxy: manual provider requires a URL when REQUIRE_PROXY", async () => {
  process.env.PROXY_PROVIDER = "manual";
  process.env.REQUIRE_PROXY = "true";
  delete require.cache[require.resolve("../src/config")];
  delete require.cache[require.resolve("../src/proxy")];
  const { resolveProxy } = require("../src/proxy");

  await assert.rejects(
    () => resolveProxy({ accountId: "a", suppliedProxyUrl: null, storedProxyUrl: null }),
    /proxyUrl is required/,
  );
  assert.equal(
    await resolveProxy({ accountId: "a", suppliedProxyUrl: "http://x:y@h:1" }),
    "http://x:y@h:1",
  );
  assert.equal(
    await resolveProxy({ accountId: "a", storedProxyUrl: "h:2:u:p" }),
    "http://u:p@h:2",
  );
  assert.equal(
    await resolveProxy({ accountId: "a", storedProxyUrl: "http://u:p@h:2" }),
    "http://u:p@h:2",
  );
});

test("resolveProxy: none provider returns null", async () => {
  process.env.PROXY_PROVIDER = "none";
  delete require.cache[require.resolve("../src/config")];
  delete require.cache[require.resolve("../src/proxy")];
  const { resolveProxy } = require("../src/proxy");
  assert.equal(await resolveProxy({ accountId: "a" }), null);
});
