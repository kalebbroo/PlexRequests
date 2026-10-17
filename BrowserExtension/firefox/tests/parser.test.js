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

test("classifies the exact 1337x removed-detail response as terminal", () => {
  const document = {
    title: "Bad Torrent ID | 1337x",
    body: { textContent: "Bad Torrent ID" },
    querySelector: () => null,
    querySelectorAll: () => []
  };

  const parsed = parser.parsePage(
    document,
    "https://1337x.to/torrent/7654321/example/");

  assert.equal(parsed.sourceKey, "1337x");
  assert.equal(parsed.pageType, "missing-detail");
  assert.deepEqual(parsed.items, []);
});

test("generic missing magnets and EXT text remain retryable rather than terminal", () => {
  const document = {
    title: "Bad Torrent ID",
    body: { textContent: "Bad Torrent ID" },
    querySelector: () => null,
    querySelectorAll: () => []
  };

  assert.equal(parser.parsePage(
    document,
    "https://ext.to/example-release-10000002/").pageType, "unsupported");

  document.title = "Example release | 1337x";
  document.body.textContent = "This page has not finished rendering.";
  assert.equal(parser.parsePage(
    document,
    "https://1337x.to/torrent/7654321/example/").pageType, "unsupported");
});

test("reads EXT.to episode tables and skips their comment links", () => {
  // Shape of an ext.to episode page: the name link and the comment link share a detail path, and the
  // comment link is marked with dwn-btn. Cell and row text mirror the rendered table, minus its buttons.
  const episodeRow = ({ id, name, seeds, leech, size }) => {
    const row = {
      textContent: `1. ${name} 9 file_upload Size${size} Files 250 Age 2 years ago Seed ${seeds} Leech ${leech}`,
      querySelector: () => null,
      querySelectorAll: () => []
    };
    const cell = {
      cloneNode: () => ({ textContent: `1. ${name}`, querySelectorAll: () => [{ remove() {} }] })
    };
    const href = `/spidey-and-his-amazing-friends-s03e01-${id}/`;
    const closest = selector => (selector === "td" ? cell : selector === "tr" ? row : null);
    return [
      {
        textContent: name,
        getAttribute: name => (name === "href" ? href : null),
        classList: { contains: () => false },
        closest
      },
      {
        textContent: "9",
        getAttribute: name => (name === "href" ? `${href}#comments` : null),
        classList: { contains: className => className === "dwn-btn" },
        closest
      }
    ];
  };
  const links = [
    ...episodeRow({ id: 10000002, name: "Spidey And His Amazing Friends S03E01 1080p HEVC x265-MeGusta", seeds: 12, leech: 3, size: "555.95 MB" }),
    ...episodeRow({ id: 10000003, name: "Spidey And His Amazing Friends S03E02 1080p HEVC x265-MeGusta", seeds: 4, leech: 0, size: "512.00 MB" })
  ];
  const document = {
    title: "Marvel's Spidey and His Amazing Friends Season 3 Episode 1 - EXT Torrents",
    body: { textContent: "Marvel's Spidey and His Amazing Friends Season 3 Episode 1" },
    querySelector: () => null,
    querySelectorAll: selector => selector === "table tbody tr td.text-left a[href]" ? links : []
  };

  const parsed = parser.parsePage(document, "https://ext.to/marvel-s-spidey-and-his-amazing-friends-s27011/?s=3&e=1");

  assert.equal(parsed.sourceKey, "ext.to");
  assert.equal(parsed.pageType, "listing");
  assert.deepEqual(parsed.items.map(item => item.externalId), ["extto:torrent:10000002", "extto:torrent:10000003"]);
  assert.equal(parsed.items[0].releaseName, "Spidey And His Amazing Friends S03E01 1080p HEVC x265-MeGusta");
  assert.equal(parsed.items[0].seeders, 12);
  assert.equal(parsed.items[0].leechers, 3);
  assert.equal(parsed.items[0].sizeBytes, parser.parseSize("555.95 MB"));
  assert.equal(parsed.items[0].needsHydration, true);
  assert.equal(parsed.items[0].sourceUrl, "https://ext.to/spidey-and-his-amazing-friends-s03e01-10000002/");
});

test("a season index page with no torrent rows stays unsupported", () => {
  const document = {
    title: "Marvel's Spidey and His Amazing Friends Season 3 - EXT Torrents",
    body: { textContent: "Season 3 60 episodes" },
    querySelector: () => null,
    querySelectorAll: () => []
  };

  const parsed = parser.parsePage(document, "https://ext.to/marvel-s-spidey-and-his-amazing-friends-s27011/?s=3");

  assert.equal(parsed.pageType, "unsupported");
  assert.deepEqual(parsed.items, []);
});
