"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const hydration = require("../hydration.js");

test("accepts supported source detail URLs and retains their adapter key", () => {
  const candidate = hydration.detailCandidate({
    externalId: "1337x:torrent:7654321",
    sourceUrl: "https://1337x.to/torrent/7654321/example/#comments",
    releaseName: "Example.Show.S01E02",
    needsHydration: true
  }, 1234);

  assert.equal(candidate.sourceUrl, "https://1337x.to/torrent/7654321/example/");
  assert.equal(candidate.createdAt, 1234);
  assert.equal(candidate.sourceKey, "1337x");
  assert.equal(candidate.needsAttention, false);
  const extCandidate = hydration.detailCandidate({
    externalId: "extto:torrent:10000002",
    sourceUrl: "https://ext.to/example-show-s01e02-10000002/#comments",
    releaseName: "Example.Show.S01E02",
    needsHydration: true
  }, 5678);
  assert.equal(extCandidate.sourceKey, "ext.to");
  assert.equal(extCandidate.sourceUrl, "https://ext.to/example-show-s01e02-10000002/");
  assert.equal(hydration.detailCandidate({
    externalId: "bad",
    sourceUrl: "https://example.com/torrent/1/",
    needsHydration: true
  }), null);
  assert.equal(hydration.detailCandidate({
    externalId: "listing",
    sourceUrl: "https://1337x.to/search/example/1/",
    needsHydration: true
  }), null);
});

test("selects the oldest due queued hydration without selecting future or failed work", () => {
  const selected = hydration.nextDue([
    { externalId: "future", state: "queued", nextAttemptAt: 200, createdAt: 1 },
    { externalId: "failed", state: "failed", nextAttemptAt: 0, createdAt: 1 },
    { externalId: "second", state: "queued", nextAttemptAt: 10, createdAt: 2 },
    { externalId: "first", state: "queued", nextAttemptAt: 10, createdAt: 1 }
  ], 100);
  assert.equal(selected.externalId, "first");
});

test("selects hydration work only for paired source adapters", () => {
  const selected = hydration.nextDue([
    { externalId: "ext", sourceKey: "ext.to", state: "queued", nextAttemptAt: 0, createdAt: 1 },
    { externalId: "x", sourceKey: "1337x", state: "queued", nextAttemptAt: 0, createdAt: 2 }
  ], 100, new Set(["1337x"]));
  assert.equal(selected.externalId, "x");
});

test("a challenge pause blocks only its own source adapter", () => {
  const eligible = hydration.eligibleSources(
    new Set(["1337x", "ext.to"]),
    { "1337x": 200, "ext.to": 50 },
    100
  );
  assert.deepEqual([...eligible], ["ext.to"]);
  assert.deepEqual(hydration.activePauses({ "1337x": 200, "ext.to": 50 }, 100), { "1337x": 200 });
});

test("navigation and retry delays are bounded", () => {
  assert.equal(hydration.navigationDelay(() => 0), 8000);
  assert.ok(hydration.navigationDelay(() => 0.999) < 15000);
  assert.equal(hydration.retryDelay(1, () => 0), 15000);
  assert.ok(hydration.retryDelay(20, () => 0) <= 30 * 60 * 1000);
  assert.equal(hydration.attentionRetryDelay(() => 0), 6 * 60 * 60 * 1000);
  assert.ok(hydration.attentionRetryDelay(() => 0.999) < 6.5 * 60 * 60 * 1000);
});

test("repeated and session-bound failures request attention without becoming terminal", () => {
  assert.equal(hydration.needsAttention("temporary network failure", 3), false);
  assert.equal(hydration.needsAttention("temporary network failure", 4), true);
  assert.equal(hydration.needsAttention("EXT.to magnet lookup returned HTTP 403.", 1), true);
  assert.equal(hydration.needsAttention("page credentials expired or were unavailable", 1), true);

  const revived = hydration.reviveLegacyFailure({
    externalId: "old-failure",
    state: "failed",
    attempts: 4,
    nextAttemptAt: Number.MAX_SAFE_INTEGER,
    startedAt: 123
  }, 5000);
  assert.equal(revived.state, "queued");
  assert.equal(revived.needsAttention, true);
  assert.equal(revived.startedAt, null);
  assert.equal(revived.nextAttemptAt, 5000);

  const interrupted = hydration.reviveInterrupted({
    externalId: "interrupted",
    state: "loading",
    attempts: 1,
    startedAt: 123
  }, null, 6000);
  assert.equal(interrupted.state, "queued");
  assert.equal(interrupted.nextAttemptAt, 6000);
  assert.match(interrupted.lastError, /Firefox restarted/);
});

test("server backlog reconciliation respects queue capacity and safely wraps cursors", () => {
  const records = [
    { state: "queued" },
    { state: "loading" },
    { state: "complete" }
  ];
  assert.equal(hydration.backlogCapacity(records, 5, 250), 3);
  assert.equal(hydration.backlogCapacity(records, 2, 250), 0);
  assert.equal(hydration.nextBacklogCursor(10, { nextCursor: 25, items: [{}] }), 25);
  assert.equal(hydration.nextBacklogCursor(25, { nextCursor: 25, items: [{}] }), 0);
  assert.equal(hydration.nextBacklogCursor(25, { nextCursor: 40, items: [] }), 0);
});

test("hydration retention reserves capacity while keeping active work and recent diagnostics", () => {
  const records = [
    { externalId: "fresh", sourceKey: "1337x", state: "queued", needsAttention: false },
    { externalId: "loading", sourceKey: "1337x", state: "loading", needsAttention: true },
    { externalId: "recent-complete", state: "complete", completedAt: 950 },
    { externalId: "old-complete", state: "complete", completedAt: 100 },
    { externalId: "x-new", sourceKey: "1337x", state: "queued", needsAttention: true, attentionAt: 990 },
    { externalId: "x-old", sourceKey: "1337x", state: "queued", needsAttention: true, attentionAt: 980 },
    { externalId: "ext-new", sourceKey: "ext.to", state: "queued", needsAttention: true, attentionAt: 970 },
    { externalId: "ext-old", sourceKey: "ext.to", state: "queued", needsAttention: true, attentionAt: 960 },
    { externalId: "expired-attention", sourceKey: "ext.to", state: "queued", needsAttention: true, attentionAt: 200 }
  ];

  const plan = hydration.retentionPlan(records, {
    now: 1000,
    completedRetentionMs: 500,
    attentionRetentionMs: 500,
    maxAttention: 3,
    maxAttentionPerSource: 2
  });

  assert.deepEqual(plan.retained.map(record => record.externalId), [
    "fresh", "loading", "recent-complete", "x-new", "x-old", "ext-new"
  ]);
  assert.deepEqual(plan.discarded.map(record => record.externalId), [
    "old-complete", "ext-old", "expired-attention"
  ]);
});

test("source limits prevent one failing adapter from consuming the diagnostic reserve", () => {
  const records = [
    { externalId: "x-1", sourceKey: "1337x", state: "failed", attentionAt: 1000 },
    { externalId: "x-2", sourceKey: "1337x", state: "queued", needsAttention: true, attentionAt: 999 },
    { externalId: "ext-1", sourceUrl: "https://ext.to/title-1/", state: "queued", needsAttention: true, attentionAt: 998 }
  ];
  const plan = hydration.retentionPlan(records, {
    now: 1000,
    completedRetentionMs: 500,
    attentionRetentionMs: 500,
    maxAttention: 2,
    maxAttentionPerSource: 1
  });

  assert.deepEqual(plan.retained.map(record => record.externalId), ["x-1", "ext-1"]);
  assert.deepEqual(plan.discarded.map(record => record.externalId), ["x-2"]);
});

test("revisiting a listing creates a fresh hydration candidate without inherited attention age", () => {
  const candidate = hydration.detailCandidate({
    externalId: "extto:torrent:10000002",
    sourceUrl: "https://ext.to/example-show-10000002/",
    releaseName: "Example Show",
    needsHydration: true
  }, 5000);

  assert.equal(candidate.needsAttention, false);
  assert.equal(candidate.attentionAt, null);
  assert.equal(candidate.createdAt, 5000);
});
