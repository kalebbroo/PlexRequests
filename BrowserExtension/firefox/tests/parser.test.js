"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const parser = require("../parser.js");

function element(text, attributes = {}) {
  return {
    textContent: text,
    childNodes: [{ nodeType: 3, textContent: text }],
    getAttribute: name => attributes[name] ?? null,
    querySelector: () => null
  };
}

test("parses binary size units and noisy counters", () => {
  assert.equal(parser.parseSize("1.4 GB 23"), 1503238554);
  assert.equal(parser.parseSize("700 MiB"), 734003200);
  assert.equal(parser.parseCount("1,234 seeders"), 1234);
  assert.equal(parser.parseSize("unknown"), null);
});

test("derives the same durable external id from listing and detail URLs", () => {
  assert.equal(
    parser.externalId("/torrent/7654321/Example-Show/", "https://1337x.to/category-search/example/TV/1/"),
    "1337x:torrent:7654321"
  );
  assert.equal(
    parser.externalId("https://1337x.to/torrent/7654321/Example-Show/", "https://1337x.to/"),
    "1337x:torrent:7654321"
  );
});

test("listing rows remain pending until a detail page supplies a magnet", () => {
  const values = {
    "td.name a[href^='/torrent/'], td.name a[href*='/torrent/']": element(
      "Example.Show.S01E02.1080p.WEB-DL.x265-GROUP",
      { href: "/torrent/7654321/example/" }
    ),
    "td.size": element("1.4 GB"),
    "td.seeds": element("52"),
    "td.leeches": element("4"),
    "td.uploader a": element("trusted-user")
  };
  const row = { querySelector: selector => values[selector] || null };
  const item = parser.parseListingRow(row, "https://1337x.to/search/example/1/", "TV");
  assert.equal(item.externalId, "1337x:torrent:7654321");
  assert.equal(item.seeders, 52);
  assert.equal(item.sizeBytes, 1503238554);
  assert.equal(item.needsHydration, true);
});

test("extracts both hexadecimal and base32 infohashes", () => {
  assert.equal(
    parser.infoHashFromMagnet("magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&dn=x"),
    "ABCDEF0123456789ABCDEF0123456789ABCDEF01"
  );
  assert.equal(parser.infoHashFromMagnet("magnet:?xt=urn:btih:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
});

test("recognizes category and category-search page layouts", () => {
  const document = {
    querySelector: () => null
  };
  assert.equal(parser.inferCategory("https://1337x.to/cat/Movies/1/", document), "Movies");
  assert.equal(parser.inferCategory("https://1337x.to/category-search/example/TV/1/", document), "TV");
});
