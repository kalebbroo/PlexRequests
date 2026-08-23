"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const captureQueue = require("../capture-queue.js");

test("permanent failures are retained separately from pending queue capacity", () => {
  const records = [
    { batchId: "queued-a", state: "queued" },
    { batchId: "failed-a", state: "failed", failedAt: 900 },
    { batchId: "queued-b", state: "queued" },
    { batchId: "failed-b", state: "failed", failedAt: 950 }
  ];

  assert.equal(captureQueue.pendingCount(records), 2);
  assert.equal(captureQueue.pendingCount(captureQueue.retentionPlan(records, {
    now: 1000,
    failureRetentionMs: 500,
    maxFailures: 10
  }).retained), 2);
});

test("retention keeps only the newest bounded diagnostics and expires poison records", () => {
  const records = [
    { batchId: "queued", state: "queued" },
    { batchId: "newest", state: "failed", failedAt: 990 },
    { batchId: "newer", state: "failed", failedAt: 980 },
    { batchId: "over-cap", state: "failed", failedAt: 970 },
    { batchId: "expired", state: "failed", failedAt: 400 },
    { batchId: "legacy", state: "failed", capturedAt: "1970-01-01T00:00:00.985Z" }
  ];

  const plan = captureQueue.retentionPlan(records, {
    now: 1000,
    failureRetentionMs: 500,
    maxFailures: 3
  });

  assert.deepEqual(plan.retained.map(record => record.batchId), ["queued", "newest", "newer", "legacy"]);
  assert.deepEqual(plan.discarded.map(record => record.batchId), ["over-cap", "expired"]);
});

test("zero retention safely removes all terminal failures without touching unknown active records", () => {
  const records = [
    { batchId: "queued", state: "queued" },
    { batchId: "future-state", state: "sending" },
    { batchId: "failed", state: "failed", failedAt: 1000 }
  ];
  const plan = captureQueue.retentionPlan(records, {
    now: 1000,
    failureRetentionMs: 0,
    maxFailures: 0
  });

  assert.deepEqual(plan.retained.map(record => record.batchId), ["queued", "future-state"]);
  assert.deepEqual(plan.discarded.map(record => record.batchId), ["failed"]);
  assert.equal(captureQueue.pendingCount(plan.retained), 2);
});
