(function exposeHydration(root, factory) {
  const sources = typeof module === "object" && module.exports
    ? require("./sources.js")
    : root.PlexRequestsCaptureSources;
  const hydration = factory(sources);
  root.PlexRequestsHydration = hydration;
  if (typeof module === "object" && module.exports) module.exports = hydration;
})(typeof globalThis !== "undefined" ? globalThis : this, function createHydration(sources) {
  "use strict";

  function isSupportedHost(hostname) {
    return Boolean(sources.byHost(hostname));
  }

  function detailCandidate(item, observedAt = Date.now()) {
    if (!item || item.needsHydration !== true || !item.externalId || !item.sourceUrl) return null;
    try {
      const url = new URL(item.sourceUrl);
      const source = sources.fromUrl(url.toString());
      if (!source || !sources.isDetailUrl(url.toString(), source.key)) return null;
      url.hash = "";
      return {
        externalId: String(item.externalId).slice(0, 512),
        sourceKey: source.key,
        sourceUrl: url.toString(),
        releaseName: String(item.releaseName || "").slice(0, 512),
        category: item.category || null,
        uploader: item.uploader || null,
        seeders: item.seeders ?? null,
        leechers: item.leechers ?? null,
        sizeBytes: item.sizeBytes ?? null,
        publishedAt: item.publishedAt || null,
        captureTorrentId: item.captureTorrentId || null,
        capturePageToken: item.capturePageToken || null,
        captureSessionId: item.captureSessionId || null,
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

  function nextDue(records, now = Date.now(), allowedSources = null) {
    return records
      .filter(item => item.state === "queued" && item.nextAttemptAt <= now
        && (!allowedSources || allowedSources.has(item.sourceKey || sources.fromUrl(item.sourceUrl)?.key)))
      .sort((a, b) => a.nextAttemptAt - b.nextAttemptAt || a.createdAt - b.createdAt)[0] || null;
  }

  return { isSupportedHost, detailCandidate, retryDelay, navigationDelay, nextDue };
});
