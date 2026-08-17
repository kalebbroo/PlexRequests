(function exposeHydration(root, factory) {
  const hydration = factory();
  root.PlexRequestsHydration = hydration;
  if (typeof module === "object" && module.exports) module.exports = hydration;
})(typeof globalThis !== "undefined" ? globalThis : this, function createHydration() {
  "use strict";

  const SUPPORTED_HOSTS = new Set([
    "1337x.to", "1337x.st", "1337x.ws", "1337x.eu", "1337x.se", "1337x.so", "1337x.is"
  ]);

  function isSupportedHost(hostname) {
    const host = String(hostname || "").toLowerCase().replace(/^www\./, "");
    return SUPPORTED_HOSTS.has(host);
  }

  function detailCandidate(item, observedAt = Date.now()) {
    if (!item || item.needsHydration !== true || !item.externalId || !item.sourceUrl) return null;
    try {
      const url = new URL(item.sourceUrl);
      if (!["http:", "https:"].includes(url.protocol) || !isSupportedHost(url.hostname)) return null;
      if (!/^\/torrent\/\d+(?:\/|$)/i.test(url.pathname)) return null;
      url.hash = "";
      return {
        externalId: String(item.externalId).slice(0, 512),
        sourceUrl: url.toString(),
        releaseName: String(item.releaseName || "").slice(0, 512),
        state: "queued",
        attempts: 0,
        createdAt: observedAt,
        startedAt: null,
        nextAttemptAt: 0,
        lastError: null
      };
    } catch {
      return null;
    }
  }

  function retryDelay(attempts, random = Math.random) {
    const base = Math.min(30 * 60 * 1000, 15_000 * (2 ** Math.min(Math.max(attempts - 1, 0), 7)));
    return base + Math.floor(random() * Math.min(base / 3, 30_000));
  }

  function navigationDelay(random = Math.random) {
    return 8_000 + Math.floor(random() * 7_000);
  }

  function nextDue(records, now = Date.now()) {
    return records
      .filter(item => item.state === "queued" && item.nextAttemptAt <= now)
      .sort((a, b) => a.nextAttemptAt - b.nextAttemptAt || a.createdAt - b.createdAt)[0] || null;
  }

  return { isSupportedHost, detailCandidate, retryDelay, navigationDelay, nextDue };
});
