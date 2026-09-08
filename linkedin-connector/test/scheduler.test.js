"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const os = require("os");
const fs = require("fs");
const path = require("path");

// Point the connector's config at a throwaway sessions dir + tight caps BEFORE
// requiring the scheduler.
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "li-sched-"));
process.env.SESSIONS_DIR = tmp;
process.env.CAP_SEND_PER_DAY = "3";
process.env.MIN_ACTION_GAP_MS = "0";
process.env.ACTION_JITTER_MS = "0";
process.env.RL_SENDS_PER_HOUR = "100";

const { Scheduler } = require("../src/scheduler");

test("enforces the rolling daily send cap", async () => {
  const s = new Scheduler("acc-1", "UTC");
  assert.equal(s.remaining("send"), 3);
  await s.schedule("send");
  await s.schedule("send");
  await s.schedule("send");
  assert.equal(s.remaining("send"), 0);
  await assert.rejects(() => s.schedule("send"), /daily send cap/);
});

test("usage persists across scheduler instances (survives a restart)", async () => {
  const a = new Scheduler("acc-2", "UTC");
  await a.schedule("send");
  await a.schedule("send");
  const b = new Scheduler("acc-2", "UTC");
  assert.equal(b.remaining("send"), 1);
});

test("cooldown blocks all actions until it expires", async () => {
  const s = new Scheduler("acc-3", "UTC");
  s.cooldown(50);
  assert.ok(s.inCooldown);
  await assert.rejects(() => s.schedule("read"), /cooldown/);
  await new Promise((r) => setTimeout(r, 60));
  assert.equal(s.inCooldown, false);
});

test("quiet hours reject outbound actions but allow reads", async () => {
  process.env.QUIET_HOURS = "0-24"; // always quiet
  const { Scheduler: S2 } = freshRequire("../src/scheduler", "../src/config");
  const s = new S2("acc-4", "UTC");
  await assert.rejects(() => s.schedule("send"), /quiet hours/);
  await s.schedule("read", { outbound: false }); // reads still flow
  delete process.env.QUIET_HOURS;
});

test("snapshot reports remaining per kind", async () => {
  const s = new Scheduler("acc-5", "UTC");
  await s.schedule("send");
  const snap = s.snapshot();
  assert.equal(snap.remaining.send, 2);
  assert.equal(snap.inCooldown, false);
});

function freshRequire(...mods) {
  for (const m of mods) delete require.cache[require.resolve(m)];
  return { Scheduler: require("../src/scheduler").Scheduler };
}
