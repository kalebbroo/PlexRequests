(function startCapture() {
  "use strict";

  const parser = globalThis.PlexRequestsCaptureParser;
  const captureQueue = globalThis.PlexRequestsCaptureQueue;
  let timer = null;
  let unsupportedTimer = null;
  let challengeTimer = null;
  let lastFingerprint = null;
  let inFlightFingerprint = null;
  let lastObservation = null;
  let extRevealKey = null;
  let extRevealObserver = null;

  function revealExtHash(pageUrl) {
    const source = sources.fromUrl(pageUrl);
    if (source?.key !== "ext.to" || !sources.isDetailUrl(pageUrl, "ext.to")) return false;
    if (parser.extInfoHash(document)) return false;

    const display = document.querySelector("#torrent-hash-display");
    const button = document.querySelector("#show-hash-btn[data-id]");
    if (!display || !button) return false;
    const key = `${pageUrl}:${button.getAttribute("data-id") || ""}`;
    if (extRevealKey === key) return true;

    extRevealKey = key;
    extRevealObserver?.disconnect();
    extRevealObserver = new MutationObserver(() => {
      if (!parser.extInfoHash(document)) return;
      extRevealObserver?.disconnect();
      extRevealObserver = null;
      clearTimeout(timeout);
      scheduleCapture(0);
    });
    const timeout = setTimeout(() => {
      if (extRevealKey === key && !parser.extInfoHash(document))
        void observe("unsupported-detail", pageUrl);
    }, 8_000);
    extRevealObserver.observe(display, { childList: true, subtree: true, characterData: true });
    try { button.click(); }
    catch {
      clearTimeout(timeout);
      extRevealObserver.disconnect();
      extRevealObserver = null;
      return false;
    }
    return true;
  }

  async function sha256(value) {
    const bytes = new TextEncoder().encode(value);
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
  }

  function publicItems(items) {
    return items.map(({ captureTorrentId, capturePageToken, captureSessionId, ...item }) => item);
  }

  async function observe(pageType, pageUrl) {
    const fingerprint = `${pageType}:${pageUrl}`;
    if (fingerprint === lastObservation) return;
    lastObservation = fingerprint;
    try {
      await browser.runtime.sendMessage({ type: "page-observation", pageType, pageUrl });
    } catch {
      // The page may be closing while the background worker advances to its next detail.
    }
  }

  async function capture(finalDetailCheck = false) {
    const parsed = parser.parsePage(document, location.href);
    const pageUrl = new URL(location.href);
    pageUrl.hash = "";
    if (parsed.pageType === "challenge") {
      clearTimeout(unsupportedTimer);
      if (!challengeTimer) {
        challengeTimer = setTimeout(() => void observe("challenge", pageUrl.toString()), 12_000);
      }
      return;
    }
    clearTimeout(challengeTimer);
    challengeTimer = null;
    if (!parsed.items.length || !["listing", "detail"].includes(parsed.pageType)) {
      if (revealExtHash(pageUrl.toString())) return;
      if (sources.isDetailUrl(pageUrl.toString())) {
        clearTimeout(unsupportedTimer);
        if (finalDetailCheck) await observe("unsupported-detail", pageUrl.toString());
        else unsupportedTimer = setTimeout(() => void capture(true), 8_000);
      }
      return;
    }

    clearTimeout(unsupportedTimer);
    lastObservation = null;
    const fingerprintSource = JSON.stringify({
      parserVersion: parser.PARSER_VERSION,
      sourceKey: parsed.sourceKey,
      pageUrl: pageUrl.toString(),
      pageType: parsed.pageType,
      items: publicItems(parsed.items).sort((a, b) => a.externalId.localeCompare(b.externalId))
    });
    const fingerprint = await sha256(fingerprintSource);
    if (fingerprint === lastFingerprint || fingerprint === inFlightFingerprint) return;
    inFlightFingerprint = fingerprint;

    try {
      const result = await browser.runtime.sendMessage({
        type: "queue-capture",
        batch: {
          batchId: `firefox-v${parser.PARSER_VERSION}-${fingerprint}`,
          sourceKey: parsed.sourceKey,
          pageUrl: pageUrl.toString(),
          pageType: parsed.pageType,
          parserVersion: parser.PARSER_VERSION,
          capturedAt: new Date().toISOString(),
          items: parsed.items
        }
      });
      if (captureQueue.isDurablyQueued(result)) lastFingerprint = fingerprint;
      else scheduleCapture(30_000);
    } catch {
      // Navigating away can tear down the content script mid-message. A page that remains open retries.
      scheduleCapture(30_000);
    } finally {
      if (inFlightFingerprint === fingerprint) inFlightFingerprint = null;
    }
  }

  function scheduleCapture(delay = 1200) {
    clearTimeout(timer);
    timer = setTimeout(() => void capture(), delay);
  }

  scheduleCapture();
  window.addEventListener("pageshow", scheduleCapture);
  const observer = new MutationObserver(scheduleCapture);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  // Observe briefly for late-rendered rows, then stop so rotating ads cannot hash an unchanged page forever.
  setTimeout(() => observer.disconnect(), 15_000);
})();
