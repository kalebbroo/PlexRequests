"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

test("protected stream downloads keep their Blob URL alive for Firefox", async () => {
  let clicked = false;
  let removed = false;
  let revoked = null;
  let cleanup = null;
  let cleanupDelay = null;
  const anchor = {
    click() { clicked = true; },
    remove() { removed = true; }
  };
  const context = {
    window: {},
    Blob,
    URL: {
      createObjectURL() { return "blob:firefox-download"; },
      revokeObjectURL(url) { revoked = url; }
    },
    document: {
      createElement(tag) {
        assert.equal(tag, "a");
        return anchor;
      },
      body: { appendChild(value) { assert.equal(value, anchor); } }
    },
    setTimeout(callback, delay) {
      cleanup = callback;
      cleanupDelay = delay;
    }
  };
  const appPath = path.join(__dirname, "..", "..", "..", "wwwroot", "app.js");
  vm.runInNewContext(fs.readFileSync(appPath, "utf8"), context);

  await context.window.plexui.downloadFileFromStream(
    "capture.zip",
    "application/zip",
    { arrayBuffer: async () => new Uint8Array([0x50, 0x4b]).buffer });

  assert.equal(clicked, true);
  assert.equal(removed, true);
  assert.equal(revoked, null);
  assert.equal(cleanupDelay, 60_000);
  cleanup();
  assert.equal(revoked, "blob:firefox-download");
});
