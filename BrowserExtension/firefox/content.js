(function startCapture() {
  "use strict";

  const parser = globalThis.PlexRequestsCaptureParser;
  let timer = null;
  let unsupportedTimer = null;
  let challengeTimer = null;
  let lastFingerprint = null;
  let lastObservation = null;

  async function sha256(value) {
    const bytes = new TextEncoder().encode(value);
    const digest = await crypto.subtle.digest("SHA-256", bytes);
    return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
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
      if (/\/torrent\//i.test(pageUrl.pathname)) {
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
      pageUrl: pageUrl.toString(),
      pageType: parsed.pageType,
      items: parsed.items.slice().sort((a, b) => a.externalId.localeCompare(b.externalId))
    });
    const fingerprint = await sha256(fingerprintSource);
    if (fingerprint === lastFingerprint) return;
    lastFingerprint = fingerprint;

    try {
      await browser.runtime.sendMessage({
        type: "queue-capture",
        batch: {
          batchId: `firefox-v${parser.PARSER_VERSION}-${fingerprint}`,
          pageUrl: pageUrl.toString(),
          pageType: parsed.pageType,
          parserVersion: parser.PARSER_VERSION,
          capturedAt: new Date().toISOString(),
          items: parsed.items
        }
      });
    } catch {
      // Navigating away can tear down the content script mid-message. The next page observation retries.
    }
  }

  function scheduleCapture() {
    clearTimeout(timer);
    timer = setTimeout(() => void capture(), 1200);
  }

  scheduleCapture();
  window.addEventListener("pageshow", scheduleCapture);
  const observer = new MutationObserver(scheduleCapture);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  // Observe briefly for late-rendered rows, then stop so rotating ads cannot hash an unchanged page forever.
  setTimeout(() => observer.disconnect(), 15_000);
})();
