(function exposeParser(root, factory) {
  const sources = typeof module === "object" && module.exports
    ? require("./sources.js")
    : root.PlexRequestsCaptureSources;
  const parser = factory(sources);
  root.PlexRequestsCaptureParser = parser;
  if (typeof module === "object" && module.exports) module.exports = parser;
})(typeof globalThis !== "undefined" ? globalThis : this, function createParser(sources) {
  "use strict";

  const PARSER_VERSION = 3;

  function cleanText(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function parseCount(value) {
    const text = cleanText(value).toLowerCase();
    const match = text.match(/([\d,.]+)\s*([km])?\b/);
    if (!match) return null;
    const amount = Number.parseFloat(match[1].replace(/,/g, ""));
    if (!Number.isFinite(amount) || amount < 0) return null;
    const multiplier = match[2] === "k" ? 1_000 : match[2] === "m" ? 1_000_000 : 1;
    return Math.round(amount * multiplier);
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
    return sources.externalId(value, pageUrl);
  }

  function infoHashFromMagnet(magnet) {
    const match = String(magnet || "").match(/(?:^|[?&])xt=urn:btih:([a-z0-9]+)/i);
    return match ? match[1] : null;
  }

  function extInfoHash(document) {
    const text = cleanText(document.querySelector("#torrent-hash-display")?.textContent);
    const labelled = text.match(/(?:btih|info[_ -]?hash|torrent[_ -]?hash|hash)[^a-f0-9]{0,20}([a-f0-9]{64}|[a-f0-9]{40}|[A-Z2-7]{32})\b/i);
    const broad = labelled || text.match(/\b([a-f0-9]{64}|[a-f0-9]{40}|[A-Z2-7]{32})\b/i);
    return broad ? broad[1].toUpperCase() : null;
  }

  function magnetFromHash(infoHash, releaseName) {
    if (!infoHash) return null;
    const name = cleanText(releaseName);
    return `magnet:?xt=urn:btih:${infoHash}${name ? `&dn=${encodeURIComponent(name)}` : ""}`;
  }

  function firstText(element, selectors) {
    for (const selector of selectors) {
      const found = element && element.querySelector && element.querySelector(selector);
      const text = cleanText(found && (found.getAttribute?.("data-title") || found.getAttribute?.("title") || found.textContent));
      if (text) return text;
    }
    return null;
  }

  function inferCategory(pageUrl, document) {
    const parts = new URL(pageUrl).pathname.split("/").filter(Boolean);
    const categoryIndex = parts.findIndex(part => part.toLowerCase() === "cat");
    const searchIndex = parts.findIndex(part => part.toLowerCase() === "category-search");
    const browseIndex = parts.findIndex(part => ["browse", "category"].includes(part.toLowerCase()));
    if (categoryIndex >= 0 && parts[categoryIndex + 1]) return parts[categoryIndex + 1];
    if (searchIndex >= 0 && parts[searchIndex + 2]) return parts[searchIndex + 2];
    if (browseIndex >= 0 && parts[browseIndex + 1]) return parts[browseIndex + 1];
    return firstText(document, [".breadcrumb li:last-child", ".breadcrumb a:last-child", "[aria-current='page']"]);
  }

  function textNodeContent(element) {
    if (!element) return "";
    const textNodes = Array.from(element.childNodes || [])
      .filter(node => node.nodeType === 3)
      .map(node => node.textContent)
      .join(" ");
    return cleanText(textNodes || element.textContent);
  }

  function labelledCount(text, label) {
    const match = cleanText(text).match(new RegExp(`${label}[a-z]*\\s*[:\\-]?\\s*([\\d,.]+\\s*[km]?)`, "i"));
    return match ? parseCount(match[1]) : null;
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
    return {
      externalId: id,
      releaseName,
      sourceUrl: absoluteUrl(href, pageUrl),
      category: category || null,
      uploader,
      seeders: parseCount(firstText(row, ["td.seeds"])),
      leechers: parseCount(firstText(row, ["td.leeches"])),
      sizeBytes: parseSize(textNodeContent(sizeCell)),
      publishedAt: time && time.getAttribute("datetime") || null,
      needsHydration: true
    };
  }

  function nearestListingContainer(link) {
    return link.closest?.("tr, li, article, [data-torrent], .torrent, .torrent-item, .list-group-item, .card")
      || link.parentElement;
  }

  function usefulLinkName(link, container) {
    const candidates = [
      link.getAttribute?.("data-title"),
      link.getAttribute?.("title"),
      link.getAttribute?.("aria-label"),
      link.textContent,
      firstText(container, ["[data-title]", ".torrent-name", ".torrent-title", ".title", "h2", "h3", "h4"])
    ];
    return candidates.map(cleanText).find(value => value && !/^(view|details?|download|torrent|more)$/i.test(value)) || null;
  }

  function extCategory(container, fallback) {
    const links = Array.from(container?.querySelectorAll?.(".related-posted a[href^='/']") || []);
    const path = links.map(link => link.getAttribute("href") || "")
      .find(href => !href.startsWith("/user/"));
    if (!path) return fallback || null;
    const part = path.split("/").filter(Boolean)[0];
    return part || fallback || null;
  }

  function parseExtListingLink(link, pageUrl, category) {
    const href = link && link.getAttribute("href");
    const id = externalId(href, pageUrl);
    const container = nearestListingContainer(link);
    const releaseName = usefulLinkName(link, container);
    if (!id || !releaseName) return null;
    const containerText = cleanText(container && container.textContent);
    const time = container?.querySelector?.("time[datetime]");
    return {
      externalId: id,
      releaseName,
      sourceUrl: absoluteUrl(href, pageUrl),
      category: extCategory(container, category),
      uploader: firstText(container, ["[class*='uploader']", "a[href*='/user/']", "a[href*='/profile/']", "[data-uc]"]),
      seeders: parseCount(firstText(container, ["[data-seeders]", "[class*='seed']"])) ?? labelledCount(containerText, "seed"),
      leechers: parseCount(firstText(container, ["[data-leechers]", "[class*='leech']"])) ?? labelledCount(containerText, "leech"),
      sizeBytes: parseSize(firstText(container, ["[data-size]", "[class*='size']"]) || containerText),
      publishedAt: time && time.getAttribute("datetime")
        || container?.querySelector?.("td:nth-child(4) span[title]")?.getAttribute("title") || null,
      needsHydration: true
    };
  }

  function detailNodes(document) {
    return document.querySelectorAll(".list li, .torrent-detail-page li, .box-info li, dl, .torrent-info, [class*='detail']");
  }

  function labelValue(document, label) {
    const pattern = new RegExp(`${label}\\s*:?\\s*([\\d,.]+\\s*[km]?)`, "i");
    for (const node of detailNodes(document)) {
      const match = cleanText(node.textContent).match(pattern);
      if (match) return match[1];
    }
    return null;
  }

  function detailSize(document) {
    for (const node of detailNodes(document)) {
      const text = cleanText(node.textContent);
      if (/\b(?:total\s+)?size\b/i.test(text)) {
        const size = parseSize(text);
        if (size) return size;
      }
    }
    return parseSize(firstText(document, ["[data-size]", "[class*='size']"]));
  }

  function parseDetail(document, pageUrl) {
    const source = sources.fromUrl(pageUrl);
    const magnetLink = document.querySelector("a[href^='magnet:']");
    const id = externalId(pageUrl, pageUrl);
    let releaseName = firstText(document, [".box-info-heading h1", ".torrent-detail-page h1", "main h1", "h1", "[data-title]"]);
    if (!releaseName) releaseName = cleanText(document.title)
      .replace(/\s*[|\-]\s*(?:1337x|ext\.to).*$/i, "");
    const revealedHash = source?.key === "ext.to" ? extInfoHash(document) : null;
    const magnet = magnetLink?.getAttribute("href") || magnetFromHash(revealedHash, releaseName);
    if (!source || !id || !releaseName || !magnet) return null;

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
      uploader: firstText(document, ["a[href*='/user/']", "a[href*='/profile/']", "[class*='uploader']"]),
      seeders: parseCount(labelValue(document, "seeders?")),
      leechers: parseCount(labelValue(document, "leechers?")),
      sizeBytes: detailSize(document),
      publishedAt: time ? time.getAttribute("datetime") : null,
      needsHydration: false
    };
  }

  function looksLikeChallenge(document) {
    const title = cleanText(document.title);
    const text = cleanText(document.body && document.body.textContent).slice(0, 1500);
    return /just a moment|checking your browser|performing security verification|verify you are human/i.test(`${title} ${text}`)
      || Boolean(document.querySelector("#challenge-form, .cf-challenge-running"));
  }

  function deduplicate(items) {
    const byId = new Map();
    for (const item of items.filter(Boolean)) {
      const current = byId.get(item.externalId);
      if (!current || (item.releaseName?.length || 0) > (current.releaseName?.length || 0)) byId.set(item.externalId, item);
    }
    return [...byId.values()];
  }

  function parsePage(document, pageUrl) {
    const source = sources.fromUrl(pageUrl);
    if (!source) return { sourceKey: null, pageType: "unsupported", items: [] };
    if (looksLikeChallenge(document)) return { sourceKey: source.key, pageType: "challenge", items: [] };
    if (sources.isDetailUrl(pageUrl, source.key)) {
      const item = parseDetail(document, pageUrl);
      return { sourceKey: source.key, pageType: item ? "detail" : "unsupported", items: item ? [item] : [] };
    }

    const category = inferCategory(pageUrl, document);
    const items = source.key === "1337x"
      ? Array.from(document.querySelectorAll("table.table-list tbody tr")).map(row => parseListingRow(row, pageUrl, category))
      : Array.from(document.querySelectorAll("table.search-table tbody a.torrent-title-link, a.torrent-title-link"))
        .map(link => parseExtListingLink(link, pageUrl, category));
    const unique = deduplicate(items);
    return { sourceKey: source.key, pageType: unique.length ? "listing" : "unsupported", items: unique };
  }

  return {
    PARSER_VERSION,
    cleanText,
    parseCount,
    parseSize,
    externalId,
    infoHashFromMagnet,
    extInfoHash,
    inferCategory,
    parseListingRow,
    parseExtListingLink,
    parseDetail,
    parsePage
  };
});
