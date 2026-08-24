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
    parser.externalId("/example-show-s03e04-10000002/", "https://ext.to/browse/?cat=2"),
    "extto:torrent:10000002"
  );
  assert.equal(
    parser.externalId("https://1337x.to/torrent/7654321/Example-Show/", "https://1337x.to/"),
    "1337x:torrent:7654321"
  );
});

test("parses EXT.to grouped TV links without depending on a fixed table layout", () => {
  const values = {
    "[data-seeders]": element("1.2K"),
    "[data-leechers]": element("34"),
    "[data-size]": element("2.5 GiB"),
    "[class*='uploader']": element("scene-group")
  };
  const container = {
    textContent: "Example Show S03E04 Seeders 1.2K Leechers 34 Size 2.5 GiB",
    querySelector: selector => values[selector] || null
  };
  const link = {
    textContent: "View",
    parentElement: container,
    closest: () => container,
    getAttribute: name => ({ href: "/example-show-s03e04-10000002/", title: "Example.Show.S03E04.1080p.WEB-DL" })[name] || null
  };

  const item = parser.parseExtListingLink(link, "https://ext.to/browse/tv/", "tv");

  assert.equal(item.externalId, "extto:torrent:10000002");
  assert.equal(item.releaseName, "Example.Show.S03E04.1080p.WEB-DL");
  assert.equal(item.seeders, 1200);
  assert.equal(item.leechers, 34);
  assert.equal(item.sizeBytes, 2684354560);
  assert.equal(item.category, "tv");
  assert.equal(item.needsHydration, true);
});

test("deduplicates repeated EXT.to torrent links on grouped show pages", () => {
  const container = {
    textContent: "Show S01E01 Size 800 MB",
    querySelector: selector => selector === ".search-magnet-btn[data-id]"
      ? { getAttribute: name => name === "data-id" ? "987" : null }
      : null,
    querySelectorAll: () => []
  };
  const link = title => ({
    textContent: title,
    parentElement: container,
    closest: () => container,
    getAttribute: name => name === "href" ? "/show-s01e01-987/" : null
  });
  const document = {
    title: "Show torrents | EXT.to",
    body: { textContent: "Show torrents" },
    querySelector: selector => selector === "meta[name='csrf-token']"
      ? { getAttribute: name => name === "content" ? "csrf-session" : null }
      : null,
    querySelectorAll: selector => {
      if (selector === "script") return [{ textContent: "window.searchPageToken = 'page-token';" }];
      if (selector === "table.search-table tbody a.torrent-title-link, a.torrent-title-link")
        return [link("Show.S01E01.720p"), link("Show.S01E01.1080p.WEB-DL")];
      return [];
    }
  };

  const parsed = parser.parsePage(document, "https://ext.to/browse/?cat=2");

  assert.equal(parsed.sourceKey, "ext.to");
  assert.equal(parsed.pageType, "listing");
  assert.equal(parsed.items.length, 1);
  assert.equal(parsed.items[0].releaseName, "Show.S01E01.1080p.WEB-DL");
  assert.equal(parsed.items[0].needsHydration, true);
  assert.equal(Object.hasOwn(parsed.items[0], "capturePageToken"), false);
  assert.equal(Object.hasOwn(parsed.items[0], "captureSessionId"), false);
});

test("parses an EXT.to detail after the sites View Hash control reveals the infohash", () => {
  const infoHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
  const values = {
    "#torrent-hash-display": element(`Torrent Hash: ${infoHash}`),
    ".box-info-heading h1": element("Example.Show.S03E04.1080p.WEB-DL")
  };
  const document = {
    title: "Example Show | EXT.to",
    body: { textContent: "Example Show torrent details" },
    querySelector: selector => values[selector] || null,
    querySelectorAll: () => []
  };

  const parsed = parser.parsePage(document, "https://ext.to/example-show-s03e04-10000002/");

  assert.equal(parsed.pageType, "detail");
  const item = assertSingle(parsed.items);
  assert.equal(item.infoHash, infoHash);
  assert.match(item.magnetUri, new RegExp(`^magnet:\\?xt=urn:btih:${infoHash}`));
  assert.match(item.magnetUri, /&dn=Example.Show.S03E04.1080p.WEB-DL$/);
  assert.equal(item.needsHydration, false);
});

function assertSingle(items) {
  assert.equal(items.length, 1);
  return items[0];
}

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
