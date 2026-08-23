"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const telemetry = require("../telemetry.js");

test("queue snapshots are source-scoped and contain no captured release data", () => {
  const snapshot = telemetry.queueSnapshot("1337x", [
    { sourceKey: "1337x", state: "queued", releaseName: "private title" },
    { pageUrl: "https://1337x.to/search/a/1/", state: "failed" },
    { sourceKey: "ext.to", state: "queued" }
  ], [
    { sourceKey: "1337x", state: "queued" },
    { sourceUrl: "https://1337x.to/torrent/1/a/", state: "loading", needsAttention: true },
    { sourceKey: "ext.to", state: "failed" }
  ], { captureEnabled: true, hydrationPauses: {} }, 1000);

  assert.deepEqual(snapshot, {
    captureEnabled: true,
    queuedUploads: 1,
    failedUploads: 1,
    pendingDetails: 1,
    attentionDetails: 1,
    hydrationPausedUntil: null
  });
  assert.ok(!JSON.stringify(snapshot).includes("private title"));
});

test("source pause wins over a legacy global pause and expired pauses disappear", () => {
  const snapshot = telemetry.queueSnapshot("ext.to", [], [], {
    captureEnabled: false,
    hydrationPauses: { "1337x": 9000, "ext.to": 8000 },
    hydrationPausedUntil: 10000
  }, 7000);
  assert.equal(snapshot.captureEnabled, false);
  assert.equal(snapshot.hydrationPausedUntil, 8000);

  const unaffected = telemetry.queueSnapshot("ext.to", [], [], {
    hydrationPauses: { "1337x": 9000 },
    hydrationPausedUntil: 10000
  }, 7000);
  assert.equal(unaffected.hydrationPausedUntil, null);

  const resumed = telemetry.queueSnapshot("ext.to", [], [], {
    hydrationPauses: { "ext.to": 6000 },
    hydrationPausedUntil: 6500
  }, 7000);
  assert.equal(resumed.hydrationPausedUntil, null);
});

test("extension versions compare numerically and malformed installed versions require an update", () => {
  assert.equal(telemetry.isVersionOlder("1.5.9", "1.6.0"), true);
  assert.equal(telemetry.isVersionOlder("1.10.0", "1.6.0"), false);
  assert.equal(telemetry.isVersionOlder("1.6", "1.6.0"), false);
  assert.equal(telemetry.isVersionOlder("unknown", "1.6.0"), true);
  assert.equal(telemetry.isVersionOlder("1.6.0", "invalid"), false);
});

test("server connection changes refresh the authoritative lease without erasing good local state", () => {
  assert.deepEqual(telemetry.connectionChanges({
    expiresAt: "2026-12-01T12:00:00Z",
    currentExtensionVersion: "1.7.0"
  }, "1.6.0"), {
    expiresAt: "2026-12-01T12:00:00Z",
    currentExtensionVersion: "1.7.0",
    updateAvailable: true
  });

  assert.deepEqual(telemetry.connectionChanges({ expiresAt: "not-a-date" }, "1.7.0"), {});
  assert.deepEqual(telemetry.connectionChanges(null, "1.7.0"), {});
});
