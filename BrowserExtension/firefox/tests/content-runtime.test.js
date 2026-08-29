"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function contentRuntime(pageUrl, document, parsed) {
  const timers = [];
  const messages = [];
  const context = vm.createContext({
    URL,
    TextEncoder,
    console,
    crypto: require("node:crypto").webcrypto,
    location: { href: pageUrl },
    window: { addEventListener() { } },
    document,
    MutationObserver: class {
      constructor(callback) { this.callback = callback; }
      observe() { }
      disconnect() { }
    },
    setTimeout(callback, delay = 0) {
      const timer = { callback, delay, cleared: false };
      timers.push(timer);
      return timer;
    },
    clearTimeout(timer) {
      if (timer) timer.cleared = true;
    },
    browser: {
      runtime: {
        async sendMessage(message) {
          messages.push(message);
          return { queued: true };
        }
      }
    }
  });
  context.globalThis = context;
  context.PlexRequestsCaptureParser = {
    PARSER_VERSION: 4,
    parsePage: () => parsed,
    extInfoHash: () => null
  };
  context.PlexRequestsCaptureQueue = { isDurablyQueued: result => result?.queued === true };

  const extension = path.join(__dirname, "..");
  vm.runInContext(fs.readFileSync(path.join(extension, "sources.js"), "utf8"), context);
  vm.runInContext(fs.readFileSync(path.join(extension, "content.js"), "utf8"), context);
  return { timers, messages };
}

test("EXT detail runtime can access its source adapter and activates View Hash", async () => {
  let clicked = 0;
  const display = { };
  const button = {
    getAttribute: name => name === "data-id" ? "21232725" : null,
    click: () => { clicked++; }
  };
  const document = {
    documentElement: {},
    querySelector: selector => ({
      "#torrent-hash-display": display,
      "#show-hash-btn[data-id]": button
    })[selector] || null
  };
  const runtime = contentRuntime(
    "https://ext.to/example-release-21232725/",
    document,
    { sourceKey: "ext.to", pageType: "unsupported", items: [] });

  const initialCapture = runtime.timers.find(timer => timer.delay === 1200);
  assert.ok(initialCapture);
  await initialCapture.callback();

  assert.equal(clicked, 1);
  assert.ok(runtime.timers.some(timer => timer.delay === 8000));
});

test("removed 1337x detail is reported without entering the generic retry path", async () => {
  const document = { documentElement: {}, querySelector: () => null };
  const pageUrl = "https://1337x.to/torrent/7654321/example/";
  const runtime = contentRuntime(
    pageUrl,
    document,
    { sourceKey: "1337x", pageType: "missing-detail", items: [] });

  const initialCapture = runtime.timers.find(timer => timer.delay === 1200);
  assert.ok(initialCapture);
  await initialCapture.callback();

  assert.equal(runtime.messages.length, 1);
  assert.equal(runtime.messages[0].type, "page-observation");
  assert.equal(runtime.messages[0].pageType, "missing-detail");
  assert.equal(runtime.messages[0].pageUrl, pageUrl);
  assert.equal(runtime.timers.some(timer => timer.delay === 8000), false);
});
