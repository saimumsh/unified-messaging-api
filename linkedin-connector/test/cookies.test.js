"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { parseJar, parseCookieHeader, parseSetCookie } = require("../src/cookies");

test("parseJar from individual fields", () => {
  const jar = parseJar({ li_at: "AQ123", jsessionid: "ajax:99" });
  assert.equal(jar.liAt, "AQ123");
  assert.equal(jar.csrfToken, "ajax:99");
  assert.match(jar.header(), /li_at=AQ123/);
  assert.match(jar.header(), /JSESSIONID="ajax:99"/);
});

test("parseJar strips quotes on JSESSIONID for the csrf token", () => {
  const jar = parseJar({ li_at: "x", jsessionid: '"ajax:42"' });
  assert.equal(jar.csrfToken, "ajax:42");
  assert.match(jar.header(), /JSESSIONID="ajax:42"/);
});

test("parseJar from a raw Cookie header keeps the whole jar", () => {
  const jar = parseJar({
    cookieHeader: 'li_at=AQ_full; JSESSIONID="ajax:7"; bcookie="v=2&abc"; lidc="b=x"; liap=true',
  });
  const map = jar.toJSON();
  assert.equal(map.li_at, "AQ_full");
  assert.equal(map.bcookie, '"v=2&abc"');
  assert.equal(map.liap, "true");
  assert.match(jar.header(), /bcookie="v=2&abc"/);
});

test("parseJar from a browser cookie array", () => {
  const jar = parseJar({
    cookies: [
      { name: "li_at", value: "AQarr" },
      { name: "JSESSIONID", value: "ajax:1" },
      { name: "bscookie", value: "x" },
    ],
  });
  assert.equal(jar.liAt, "AQarr");
  assert.equal(jar.toJSON().bscookie, "x");
});

test("parseJar throws without li_at or JSESSIONID", () => {
  assert.throws(() => parseJar({ li_at: "only" }), /JSESSIONID/);
  assert.throws(() => parseJar({ jsessionid: "ajax:1" }), /li_at/);
});

test("merge folds a rotated cookie in", () => {
  const jar = parseJar({ li_at: "a", jsessionid: "ajax:1" });
  jar.merge({ JSESSIONID: "ajax:2", lidc: "b=fresh" });
  assert.equal(jar.csrfToken, "ajax:2");
  assert.equal(jar.toJSON().lidc, "b=fresh");
});

test("parseCookieHeader ignores a leading 'Cookie:' label", () => {
  const map = parseCookieHeader("Cookie: li_at=z; JSESSIONID=ajax:3");
  assert.equal(map.li_at, "z");
  assert.equal(map.JSESSIONID, "ajax:3");
});

test("parseSetCookie extracts name=value from Set-Cookie lines", () => {
  const map = parseSetCookie([
    "JSESSIONID=ajax:9; Path=/; Secure; HttpOnly",
    "lidc=\"b=OB1:s=O\"; Domain=.linkedin.com; Path=/",
  ]);
  assert.equal(map.JSESSIONID, "ajax:9");
  assert.equal(map.lidc, '"b=OB1:s=O"');
});
