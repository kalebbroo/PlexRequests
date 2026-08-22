(function exposeSources(root, factory) {
  const sources = factory();
  root.PlexRequestsCaptureSources = sources;
  if (typeof module === "object" && module.exports) module.exports = sources;
})(typeof globalThis !== "undefined" ? globalThis : this, function createSources() {
  "use strict";

  const definitions = [
    {
      key: "1337x",
      displayName: "1337x",
      hosts: ["1337x.to", "1337x.st", "1337x.ws", "1337x.eu", "1337x.se", "1337x.so", "1337x.is"],
      detailPath: /^\/torrent\/\d+(?:\/|$)/i
    },
    {
      key: "ext.to",
      displayName: "EXT.to",
      hosts: ["ext.to"],
      detailPath: /^\/(?!browse(?:\/|$)|search(?:\/|$)|ajax(?:\/|$)|cdn-cgi(?:\/|$))[^/]*-\d+\/?$/i
    }
  ];

  function normalizeHost(value) {
    return String(value || "").trim().toLowerCase().replace(/^www\./, "").replace(/\.$/, "");
  }

  function byKey(key) {
    const normalized = String(key || "").trim().toLowerCase();
    return definitions.find(source => source.key.toLowerCase() === normalized) || null;
  }

  function byHost(hostname) {
    const host = normalizeHost(hostname);
    return definitions.find(source => source.hosts.some(allowed => host === allowed || host.endsWith(`.${allowed}`))) || null;
  }

  function fromUrl(value) {
    try { return byHost(new URL(value).hostname); } catch { return null; }
  }

  function isDetailUrl(value, expectedKey = null) {
    try {
      const url = new URL(value);
      const source = expectedKey ? byKey(expectedKey) : byHost(url.hostname);
      return Boolean(source && byHost(url.hostname)?.key === source.key && source.detailPath.test(url.pathname));
    } catch {
      return false;
    }
  }

  function externalId(value, pageUrl) {
    try {
      const url = new URL(value, pageUrl);
      const source = byHost(url.hostname);
      if (!source || !source.detailPath.test(url.pathname)) return null;
      const match = source.key === "1337x"
        ? url.pathname.match(/^\/torrent\/([^/]+)/i)
        : url.pathname.match(/-(\d+)\/?$/);
      if (!match) return null;
      const id = decodeURIComponent(match[1]).trim().toLowerCase();
      if (!id) return null;
      const prefix = source.key === "1337x" ? "1337x" : "extto";
      return `${prefix}:torrent:${id}`;
    } catch {
      return null;
    }
  }

  return { definitions, normalizeHost, byKey, byHost, fromUrl, isDetailUrl, externalId };
});
