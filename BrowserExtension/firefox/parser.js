(function exposeParser(root, factory) {
  const parser = factory();
  root.PlexRequestsCaptureParser = parser;
  if (typeof module === "object" && module.exports) module.exports = parser;
})(typeof globalThis !== "undefined" ? globalThis : this, function createParser() {
  "use strict";

  const PARSER_VERSION = 1;

  function cleanText(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function parseCount(value) {
    const digits = cleanText(value).replace(/[^0-9]/g, "");
    return digits ? Number.parseInt(digits, 10) : null;
  }

  function parseSize(value) {
    const match = cleanText(value).match(/([\d,.]+)\s*(TiB|GiB|MiB|KiB|TB|GB|MB|KB|B)\b/i);
    if (!match) return null;
    const amount = Number.parseFloat(match[1].replace(/,/g, ""));
    if (!Number.isFinite(amount) || amount <= 0) return null;
    const multiplier = {
      tib: 1024 ** 4, tb: 1024 ** 4,
      gib: 1024 ** 3, gb: 1024 ** 3,
      mib: 1024 ** 2, mb: 1024 ** 2,
      kib: 1024, kb: 1024,
      b: 1
    }[match[2].toLowerCase()];
    return Math.round(amount * multiplier);
  }

  function absoluteUrl(value, pageUrl) {
    try {
      const url = new URL(value, pageUrl);
      url.hash = "";
      return url.toString();
    } catch {
      return null;
    }
  }

  function externalId(value, pageUrl) {
    const absolute = absoluteUrl(value, pageUrl);
    if (!absolute) return null;
    const url = new URL(absolute);
    const numeric = url.pathname.match(/\/torrent\/(\d+)(?:\/|$)/i);
    if (numeric) return `1337x:torrent:${numeric[1]}`;
    const path = url.pathname.replace(/\/+$/, "").toLowerCase();
    return path ? `1337x:path:${path}` : null;
  }

  function infoHashFromMagnet(magnet) {
    const match = String(magnet || "").match(/(?:^|[?&])xt=urn:btih:([a-z0-9]+)/i);
    return match ? match[1] : null;
  }

  function firstText(element, selectors) {
    for (const selector of selectors) {
      const found = element.querySelector(selector);
      const text = cleanText(found && found.textContent);
      if (text) return text;
    }
    return null;
  }

  function inferCategory(pageUrl, document) {
    const parts = new URL(pageUrl).pathname.split("/").filter(Boolean);
    const categoryIndex = parts.findIndex(part => part.toLowerCase() === "cat");
    const searchIndex = parts.findIndex(part => part.toLowerCase() === "category-search");
    if (categoryIndex >= 0 && parts[categoryIndex + 1]) return parts[categoryIndex + 1];
    if (searchIndex >= 0 && parts[searchIndex + 2]) return parts[searchIndex + 2];
    const breadcrumb = firstText(document, [".breadcrumb li:last-child", ".breadcrumb a:last-child"]);
    return breadcrumb;
  }

  function textNodeContent(element) {
    if (!element) return "";
    const textNodes = Array.from(element.childNodes || [])
      .filter(node => node.nodeType === 3)
      .map(node => node.textContent)
      .join(" ");
    return cleanText(textNodes || element.textContent);
  }

  function parseListingRow(row, pageUrl, category) {
    const link = row.querySelector("td.name a[href^='/torrent/'], td.name a[href*='/torrent/']")
      || row.querySelector("td.name a:nth-of-type(2)");
    const href = link && link.getAttribute("href");
    const releaseName = cleanText(link && link.textContent);
    const id = externalId(href, pageUrl);
    if (!id || !releaseName) return null;

    const sizeCell = row.querySelector("td.size");
    const uploader = firstText(row, ["td.uploader a", "a[href*='/user/']"]);
    const time = row.querySelector("time[datetime]");
    const publishedAt = time && time.getAttribute("datetime");
    return {
      externalId: id,
      releaseName,
      sourceUrl: absoluteUrl(href, pageUrl),
      category: category || null,
      uploader,
      seeders: parseCount(firstText(row, ["td.seeds"])),
      leechers: parseCount(firstText(row, ["td.leeches"])),
      sizeBytes: parseSize(textNodeContent(sizeCell)),
      publishedAt: publishedAt || null,
      needsHydration: true
    };
  }

  function labelValue(document, label) {
    const pattern = new RegExp(`${label}\\s*:?\\s*([\\d,]+)`, "i");
    const nodes = document.querySelectorAll(".list li, .torrent-detail-page li, .box-info li");
    for (const node of nodes) {
      const match = cleanText(node.textContent).match(pattern);
      if (match) return match[1];
    }
    return null;
  }

  function detailSize(document) {
    const nodes = document.querySelectorAll(".list li, .torrent-detail-page li, .box-info li");
    for (const node of nodes) {
      const text = cleanText(node.textContent);
      if (/\b(?:total\s+)?size\b/i.test(text)) {
        const size = parseSize(text);
        if (size) return size;
      }
    }
    return null;
  }

  function parseDetail(document, pageUrl) {
    const magnetLink = document.querySelector("a[href^='magnet:']");
    const magnet = magnetLink && magnetLink.getAttribute("href");
    const id = externalId(pageUrl, pageUrl);
    let releaseName = firstText(document, [".box-info-heading h1", "main h1", "h1"]);
    if (!releaseName) releaseName = cleanText(document.title).replace(/\s*\|\s*1337x.*$/i, "");
    if (!id || !releaseName || !magnet) return null;

    const imdbLink = document.querySelector("a[href*='imdb.com/title/tt']");
    const imdbMatch = imdbLink && imdbLink.getAttribute("href").match(/\b(tt\d+)\b/i);
    const time = document.querySelector("time[datetime]");
    return {
      externalId: id,
      releaseName,
      sourceUrl: absoluteUrl(pageUrl, pageUrl),
      infoHash: infoHashFromMagnet(magnet),
      magnetUri: magnet,
      imdbId: imdbMatch ? imdbMatch[1].toLowerCase() : null,
      category: inferCategory(pageUrl, document),
      uploader: firstText(document, ["a[href*='/user/']"]),
      seeders: parseCount(labelValue(document, "seeders?")),
      leechers: parseCount(labelValue(document, "leechers?")),
      sizeBytes: detailSize(document),
      publishedAt: time ? time.getAttribute("datetime") : null,
      needsHydration: false
    };
  }

  function looksLikeChallenge(document) {
    const title = cleanText(document.title);
    const text = cleanText(document.body && document.body.textContent).slice(0, 1000);
    return /just a moment|checking your browser/i.test(`${title} ${text}`)
      || Boolean(document.querySelector("#challenge-form, .cf-challenge-running"));
  }

  function parsePage(document, pageUrl) {
    if (looksLikeChallenge(document)) return { pageType: "challenge", items: [] };
    if (/\/torrent\//i.test(new URL(pageUrl).pathname)) {
      const item = parseDetail(document, pageUrl);
      return { pageType: item ? "detail" : "unsupported", items: item ? [item] : [] };
    }

    const category = inferCategory(pageUrl, document);
    const rows = Array.from(document.querySelectorAll("table.table-list tbody tr"));
    const items = rows.map(row => parseListingRow(row, pageUrl, category)).filter(Boolean);
    return { pageType: items.length ? "listing" : "unsupported", items };
  }

  return {
    PARSER_VERSION,
    cleanText,
    parseCount,
    parseSize,
    externalId,
    infoHashFromMagnet,
    inferCategory,
    parseListingRow,
    parseDetail,
    parsePage
  };
});
