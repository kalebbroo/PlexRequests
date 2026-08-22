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

test("navigation and retry delays are bounded", () => {
  assert.equal(hydration.navigationDelay(() => 0), 8000);
  assert.ok(hydration.navigationDelay(() => 0.999) < 15000);
  assert.equal(hydration.retryDelay(1, () => 0), 15000);
  assert.ok(hydration.retryDelay(20, () => 0) <= 30 * 60 * 1000);
});
