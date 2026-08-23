"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const sources = require("../sources.js");

test("routes supported hosts to isolated source adapters", () => {
  assert.equal(sources.fromUrl("https://1337x.to/search/show/1/").key, "1337x");
  assert.equal(sources.fromUrl("https://www.ext.to/browse/tv/").key, "ext.to");
  assert.equal(sources.fromUrl("https://notext.to/torrent/1"), null);
  assert.equal(sources.isDetailUrl("https://ext.to/example-show-s01e02-10000002/", "ext.to"), true);
  assert.equal(sources.isDetailUrl("https://1337x.to/torrent/123/show", "ext.to"), false);
  assert.equal(sources.byKey("1337x").durableBacklog, true);
  assert.equal(sources.byKey("ext.to").durableBacklog, false);
});
